using System.Collections.Concurrent;
using System.Security.Principal;
using GDM.Models;
using GDM.Services.Abstractions;
using HIDMaestro;

namespace GDM.Services;

public sealed class HidMaestroVirtualGamepadService : IVirtualGamepadService
{
    private readonly IGamepadInputService _inputService;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, VirtualControllerSession> _sessions =
        new(StringComparer.Ordinal);
    private bool _isDisposed;

    public bool IsRunning => !_sessions.IsEmpty;
    public IReadOnlyCollection<string> RunningDeviceKeys => _sessions.Keys.ToArray();
    public bool IsAdministrator => IsAdministratorProcess();
    public string? SourceDeviceName => _sessions.Values.FirstOrDefault()?.Device.Name;

    public HidMaestroVirtualGamepadService(IGamepadInputService inputService)
    {
        _inputService = inputService ?? throw new ArgumentNullException(nameof(inputService));
    }

    public bool IsRunningFor(string deviceKey) =>
        _sessions.ContainsKey(deviceKey);

    public bool RebindDevice(string previousDeviceKey, ConnectedGamepad device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousDeviceKey);
        ArgumentNullException.ThrowIfNull(device);
        if (previousDeviceKey == device.DeviceKey)
        {
            return IsRunningFor(previousDeviceKey);
        }

        _lifecycleGate.Wait();
        try
        {
            if (!_sessions.TryRemove(previousDeviceKey, out VirtualControllerSession? session))
            {
                return false;
            }

            ConnectedGamepad previousDevice = session.Device;
            session.Device = device;
            if (_sessions.TryAdd(device.DeviceKey, session))
            {
                return true;
            }

            session.Device = previousDevice;
            if (!_sessions.TryAdd(previousDeviceKey, session))
            {
                session.Context.Dispose();
            }

            return false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(
        ConnectedGamepad device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(
            device.DeviceKey,
            cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException("El mando seleccionado ya no está conectado.");
        }

        await StartAsync(device, snapshot, null, cancellationToken);
    }

    public async Task StartAsync(
        ConnectedGamepad device,
        GamepadInputSnapshot initialSnapshot,
        ControllerProfile? mappingProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        if (!initialSnapshot.IsStandardGameController)
        {
            throw new InvalidOperationException(
                "SDL no reconoce un mapeo estándar para este mando. No se iniciará una salida virtual con controles sin asignar.");
        }

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_sessions.ContainsKey(device.DeviceKey))
            {
                throw new InvalidOperationException(
                    "Este mando físico ya tiene una salida virtual en ejecución.");
            }

            if (!IsAdministratorProcess())
            {
                throw new UnauthorizedAccessException(
                    "HIDMaestro necesita permisos de administrador para crear un mando virtual.");
            }

            VirtualControllerSession session = await Task.Run(() =>
            {
                var context = new HMContext();
                try
                {
                    context.LoadDefaultProfiles();
                    HMProfile outputProfile = context.GetProfile("xbox-360-wired")
                        ?? throw new InvalidOperationException(
                            "HIDMaestro no incluye el perfil de mando virtual Xbox 360.");
                    if (!context.IsDriverInstalled)
                    {
                        context.InstallDriver();
                    }

                    if (!context.IsDriverInstalled)
                    {
                        throw new InvalidOperationException(
                            "HIDMaestro no pudo confirmar que su controlador quedó instalado.");
                    }

                    HMController controller = context.CreateController(
                        outputProfile,
                        $"GDM-{Guid.NewGuid():N}");
                    return new VirtualControllerSession(
                        context,
                        controller,
                        outputProfile,
                        device,
                        mappingProfile);
                }
                catch
                {
                    context.Dispose();
                    throw;
                }
            }, cancellationToken);

            try
            {
                session.Controller.SubmitState(
                    HidMaestroGamepadMapper.CreateState(
                        initialSnapshot,
                        session.OutputProfile,
                        mappingProfile));
            }
            catch
            {
                session.Context.Dispose();
                throw;
            }

            if (!_sessions.TryAdd(device.DeviceKey, session))
            {
                session.Context.Dispose();
                throw new InvalidOperationException(
                    "No se pudo registrar la salida virtual para este mando físico.");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void SubmitInputSnapshot(ConnectedGamepad device, GamepadInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_sessions.TryGetValue(device.DeviceKey, out VirtualControllerSession? session))
        {
            Submit(session, snapshot, session.MappingProfile);
        }
    }

    public void SubmitInputSnapshot(
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? mappingProfile)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_sessions.TryGetValue(device.DeviceKey, out VirtualControllerSession? session))
        {
            Submit(session, snapshot, mappingProfile);
        }
    }

    public async Task StopAsync(
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryRemove(deviceKey, out VirtualControllerSession? session))
            {
                session.Context.Dispose();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            foreach (string deviceKey in _sessions.Keys)
            {
                if (_sessions.TryRemove(deviceKey, out VirtualControllerSession? session))
                {
                    session.Context.Dispose();
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        StopAllAsync().GetAwaiter().GetResult();
        _isDisposed = true;
        _lifecycleGate.Dispose();
    }

    private static void Submit(
        VirtualControllerSession session,
        GamepadInputSnapshot snapshot,
        ControllerProfile? mappingProfile)
    {
        session.Controller.SubmitState(
            HidMaestroGamepadMapper.CreateState(
                snapshot,
                session.OutputProfile,
                mappingProfile));
        session.MappingProfile = mappingProfile;
    }

    private static bool IsAdministratorProcess()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class VirtualControllerSession(
        HMContext context,
        HMController controller,
        HMProfile outputProfile,
        ConnectedGamepad device,
        ControllerProfile? mappingProfile)
    {
        public HMContext Context { get; } = context;
        public HMController Controller { get; } = controller;
        public HMProfile OutputProfile { get; } = outputProfile;
        public ConnectedGamepad Device { get; set; } = device;
        public ControllerProfile? MappingProfile { get; set; } = mappingProfile;
    }
}
