using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Principal;
using GDM.Models;
using GDM.Services.Abstractions;
using HIDMaestro;

namespace GDM.Services;

public sealed class HidMaestroVirtualGamepadService : IVirtualGamepadService
{
    private readonly IGamepadInputService _inputService;
    private readonly Func<string?>? _xbox360Variant;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, VirtualControllerSession> _sessions =
        new(StringComparer.Ordinal);
    private bool _isDisposed;

    public event Action<string, int, int>? RumbleReceived;

    public bool IsRunning => !_sessions.IsEmpty;
    public IReadOnlyCollection<string> RunningDeviceKeys => _sessions.Keys.ToArray();
    public bool IsAdministrator => IsAdministratorProcess();
    public string? SourceDeviceName => _sessions.Values.FirstOrDefault()?.Device.Name;

    public HidMaestroVirtualGamepadService(
        IGamepadInputService inputService,
        Func<string?>? xbox360Variant = null)
    {
        _inputService = inputService ?? throw new ArgumentNullException(nameof(inputService));
        _xbox360Variant = xbox360Variant;
    }

    public bool CleanupOrphanedVirtualControllers()
    {
        if (_isDisposed)
        {
            return false;
        }

        try
        {
            HMContext.RemoveAllVirtualControllers();
            return true;
        }
        catch
        {
            return false;
        }
    }
    public bool IsRunningFor(string deviceKey) =>
        _sessions.ContainsKey(deviceKey);

    public string? GetRunningVirtualGamepadType(string deviceKey) =>
        _sessions.TryGetValue(deviceKey, out VirtualControllerSession? session)
            ? session.VirtualGamepadType
            : null;

    public GamepadInputSnapshot? TryGetVirtualOutputSnapshot(ConnectedGamepad virtualDevice)
    {
        ArgumentNullException.ThrowIfNull(virtualDevice);
        VirtualControllerSession? session = FindSessionForVirtualDevice(virtualDevice);
        return session?.LastSubmittedState is { } state
            ? HidMaestroGamepadMapper.CreateDisplaySnapshot(
                state, session.OutputProfile, session.VirtualGamepadType)
            : null;
    }

    private VirtualControllerSession? FindSessionForVirtualDevice(ConnectedGamepad virtualDevice)
    {
        VirtualControllerSession? match = null;
        int matches = 0;
        foreach (VirtualControllerSession candidate in _sessions.Values)
        {
            if (candidate.LastSubmittedState is null ||
                candidate.OutputProfile.VendorId != (ushort)(virtualDevice.VendorId ?? 0) ||
                candidate.OutputProfile.ProductId != (ushort)(virtualDevice.ProductId ?? 0))
            {
                continue;
            }

            if (MatchesControllerIdentity(virtualDevice, candidate.ControllerName))
            {
                return candidate;
            }

            match ??= candidate;
            matches++;
        }

        return match;
    }

    private static bool MatchesControllerIdentity(ConnectedGamepad device, string controllerName) =>
        ContainsOrdinal(device.Name, controllerName) ||
        ContainsOrdinal(device.SerialNumber, controllerName) ||
        ContainsOrdinal(device.DevicePath, controllerName);

    private static bool ContainsOrdinal(string? haystack, string needle) =>
        haystack is not null &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

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
        string virtualGamepadType,
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

        await StartAsync(device, snapshot, null, virtualGamepadType, cancellationToken);
    }

    public async Task StartAsync(
        ConnectedGamepad device,
        GamepadInputSnapshot initialSnapshot,
        ControllerProfile? mappingProfile,
        string virtualGamepadType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        string normalizedType = VirtualGamepadTypes.Normalize(virtualGamepadType);
        if (!initialSnapshot.IsStandardGameController)
        {
            throw new InvalidOperationException(
                "SDL no reconoce un mapeo estándar para este mando. No se iniciará una salida virtual con controles sin asignar.");
        }

        if (!IsAdministratorProcess())
        {
            throw new UnauthorizedAccessException(
                "HIDMaestro necesita permisos de administrador para crear un mando virtual.");
        }

        // La creación del contexto HIDMaestro es la parte lenta (driver +
        // dispositivo virtual). Se hace fuera del gate para no bloquear
        // RebindDevice/StopAsync mientras tanto; el registro sigue siendo
        // atómico dentro del gate.
        VirtualControllerSession session = await Task.Run(() =>
        {
            var context = new HMContext();
            try
            {
                context.LoadDefaultProfiles();
                HMProfile outputProfile = context.GetProfile(
                        VirtualGamepadTypes.HidMaestroProfileId(
                            normalizedType,
                            _xbox360Variant?.Invoke()))
                    ?? throw new InvalidOperationException(
                        $"HIDMaestro no incluye el perfil de mando virtual {VirtualGamepadTypes.DisplayName(normalizedType)}.");
                if (!context.IsDriverInstalled)
                {
                    context.InstallDriver();
                }

                if (!context.IsDriverInstalled)
                {
                    throw new InvalidOperationException(
                        "HIDMaestro no pudo confirmar que su controlador quedó instalado.");
                }

                if (string.Equals(normalizedType, VirtualGamepadTypes.DualShock4, StringComparison.Ordinal))
                {
                    TryArmExtendedReport(outputProfile);
                }

                string controllerName = $"GDM-{Guid.NewGuid():N}";
                HMController controller = context.CreateController(
                    outputProfile,
                    controllerName);
                return new VirtualControllerSession(
                    context,
                    controller,
                    outputProfile,
                    device,
                    mappingProfile,
                    normalizedType,
                    controllerName);
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }, cancellationToken);

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_isDisposed || _sessions.ContainsKey(device.DeviceKey))
            {
                session.Context.Dispose();
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                throw new InvalidOperationException(
                    "Este mando físico ya tiene una salida virtual en ejecución.");
            }

            session.Controller.OutputReceived += (_, packet) => OnOutputPacket(session, packet);
            session.Controller.OutputDecoded += (_, args) => OnOutputDecoded(session, args);

            try
            {
                HMGamepadState initialState = HidMaestroGamepadMapper.CreateState(
                    initialSnapshot,
                    session.OutputProfile,
                    mappingProfile,
                    session.VirtualGamepadType);
                session.Controller.SubmitState(initialState);
                session.LastSubmittedState = initialState;
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
        ControllerProfile? mappingProfile,
        IReadOnlyCollection<string>? additionalTargetButtons = null,
        int? motionIntervalMs = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_sessions.TryGetValue(device.DeviceKey, out VirtualControllerSession? session))
        {
            Submit(session, snapshot, mappingProfile, additionalTargetButtons, motionIntervalMs);
        }
    }

    public async Task StopAsync(
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        VirtualControllerSession? session;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            _sessions.TryRemove(deviceKey, out session);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (session is null)
        {
            return;
        }

        SubmitNeutralState(session);
        Task disposeTask = Task.Run(() => session.Context.Dispose());
        await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(20)));
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        VirtualControllerSession[] sessions;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            sessions = _sessions.Values.ToArray();
            foreach (string deviceKey in _sessions.Keys)
            {
                _sessions.TryRemove(deviceKey, out _);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        var disposeTasks = new List<Task>(sessions.Length);
        foreach (VirtualControllerSession session in sessions)
        {
            SubmitNeutralState(session);
            disposeTasks.Add(Task.Run(() => session.Context.Dispose()));
        }

        // Los contextos HIDMaestro pueden tardar en liberarse; los
        // destruimos en paralelo y esperamos como máximo 20 s en total.
        if (disposeTasks.Count > 0)
        {
            await Task.WhenAll(disposeTasks.Select(
                task => Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)))));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        VirtualControllerSession[] sessions = _sessions.Values.ToArray();
        foreach (string deviceKey in _sessions.Keys)
        {
            _sessions.TryRemove(deviceKey, out _);
        }

        foreach (VirtualControllerSession session in sessions)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    session.Context.Dispose();
                }
                catch
                {
                }
            });
        }

        _lifecycleGate.Dispose();
    }


    private static void TryArmExtendedReport(HMProfile profile)
    {
        try
        {
            object? inner = profile.GetType()
                .GetProperty("Inner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(profile);
            object? extended = inner?.GetType()
                .GetProperty("ExtendedReport", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(inner);
            extended?.GetType()
                .GetProperty("AlwaysArmed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(extended, true);
        }
        catch
        {
        }
    }

    private void OnOutputPacket(VirtualControllerSession session, HMOutputPacket packet)
    {
        ReadOnlySpan<byte> data = packet.Data.Span;
        if (packet.Source == HMOutputSource.XInput && data.Length >= 4)
        {
            EmitRumble(session, data[2], data[3]);
            return;
        }

        if (packet.Source is HMOutputSource.HidOutput or HMOutputSource.HidFeature)
        {
            TryEmitDs4Rumble(session, packet.ReportId, data);
        }
    }

    // DualShock 4: el host envía el reporte de efectos por HID.
    // packet.Data excluye el ReportId.
    //   USB (ID 5):  Data[0]=0x07 magic; Data[3]=motor derecho (alta freq);
    //                Data[4]=motor izquierdo (baja freq); Data[5..7]=LED RGB.
    //   BT  (ID 17): Data[0] bits 0xC0; Data[2]=0x03 flags; Data[5]=derecho;
    //                Data[6]=izquierdo; CRC al final (se ignora).
    private void TryEmitDs4Rumble(VirtualControllerSession session, byte reportId, ReadOnlySpan<byte> data)
    {
        int rightIndex;
        int leftIndex;
        if (reportId == 5 && data.Length >= 5 && data[0] == 0x07)
        {
            rightIndex = 3;
            leftIndex = 4;
        }
        else if (reportId == 17 && data.Length >= 7 && (data[0] & 0xC0) == 0xC0)
        {
            rightIndex = 5;
            leftIndex = 6;
        }
        else
        {
            return;
        }

        // ucRumbleLeft = motor grande de baja frecuencia; ucRumbleRight = pequeño de alta.
        EmitRumble(session, data[leftIndex], data[rightIndex]);
    }

    private void OnOutputDecoded(VirtualControllerSession session, HMOutputDecodedEventArgs args)
    {
        if (args.Fields.TryGetValue("leftMotor", out object? low) &&
            args.Fields.TryGetValue("rightMotor", out object? high))
        {
            EmitRumble(session, ToByte(low), ToByte(high));
        }
    }

    private void EmitRumble(VirtualControllerSession session, int low, int high)
    {
        long now = Environment.TickCount64;
        if (now - session.LastRumbleEmitMs < 50)
        {
            return;
        }

        session.LastRumbleEmitMs = now;
        if (RumbleReceived is null)
        {
            return;
        }

        foreach (Action<string, int, int> handler in RumbleReceived.GetInvocationList())
        {
            try
            {
                handler(session.Device.DeviceKey, low, high);
            }
            catch
            {
            }
        }
    }

    private static int ToByte(object? value) => value switch
    {
        byte b => b,
        _ => Math.Clamp(Convert.ToInt32(value ?? 0), 0, 255),
    };

    private static void Submit(
        VirtualControllerSession session,
        GamepadInputSnapshot snapshot,
        ControllerProfile? mappingProfile,
        IReadOnlyCollection<string>? additionalTargetButtons = null,
        int? motionIntervalMs = null)
    {
        HMGamepadState state = HidMaestroGamepadMapper.CreateState(
            snapshot,
            session.OutputProfile,
            mappingProfile,
            session.VirtualGamepadType,
            additionalTargetButtons,
            ResolveMotion(session, snapshot.Motion, motionIntervalMs));
        if (session.LastSubmittedState is { } lastState && StatesEqual(lastState, state))
        {
            session.MappingProfile = mappingProfile;
            return;
        }

        session.Controller.SubmitState(state);
        session.LastSubmittedState = state;
        session.MappingProfile = mappingProfile;
    }

    private static void SubmitNeutralState(VirtualControllerSession session)
    {
        try
        {
            session.Controller.SubmitState(new HMGamepadState
            {
                Axes = HMGamepadStateHelpers.StandardAxes(
                    session.OutputProfile,
                    leftStickX: 0.5f,
                    leftStickY: 0.5f,
                    rightStickX: 0.5f,
                    rightStickY: 0.5f,
                    leftTrigger: 0f,
                    rightTrigger: 0f),
                Buttons = HMButton.None,
                Hat = HMHat.None
            });
            session.LastSubmittedState = null;
        }
        catch
        {
        }
    }

    private static MotionSensorData? ResolveMotion(
        VirtualControllerSession session,
        MotionSensorData? fresh,
        int? intervalMs)
    {
        if (!string.Equals(session.VirtualGamepadType, VirtualGamepadTypes.DualShock4, StringComparison.Ordinal) ||
            intervalMs is null or <= 0)
        {
            session.LastMotion = null;
            session.LastMotionSubmitMs = 0;
            return null;
        }

        long now = Environment.TickCount64;
        if (fresh is null)
        {
            return session.LastMotion;
        }

        if (session.LastMotionSubmitMs == 0 ||
            now - session.LastMotionSubmitMs >= intervalMs.Value)
        {
            session.LastMotion = fresh;
            session.LastMotionSubmitMs = now;
            return fresh;
        }

        return session.LastMotion ?? fresh;
    }

    private static bool StatesEqual(HMGamepadState first, HMGamepadState second)
    {
        if (first.Buttons != second.Buttons || first.Hat != second.Hat ||
            first.Axes is null || second.Axes is null ||
            first.Axes.Count != second.Axes.Count)
        {
            return false;
        }

        foreach ((HMAxis axis, float value) in first.Axes)
        {
            if (!second.Axes.TryGetValue(axis, out float other) || other != value)
            {
                return false;
            }
        }

        return first.AccelX == second.AccelX && first.AccelY == second.AccelY &&
            first.AccelZ == second.AccelZ && first.GyroPitch == second.GyroPitch &&
            first.GyroYaw == second.GyroYaw && first.GyroRoll == second.GyroRoll &&
            first.AccelGX == second.AccelGX && first.AccelGY == second.AccelGY &&
            first.AccelGZ == second.AccelGZ && first.GyroDpsX == second.GyroDpsX &&
            first.GyroDpsY == second.GyroDpsY && first.GyroDpsZ == second.GyroDpsZ &&
            first.BatteryLevel == second.BatteryLevel &&
            first.BatteryCharging == second.BatteryCharging &&
            first.BatteryFull == second.BatteryFull &&
            TouchFingerEqual(
                first.TouchpadFinger0Active, first.TouchpadFinger0X, first.TouchpadFinger0Y,
                first.TouchpadFinger0Id, first.TouchpadFinger0Pressure,
                second.TouchpadFinger0Active, second.TouchpadFinger0X, second.TouchpadFinger0Y,
                second.TouchpadFinger0Id, second.TouchpadFinger0Pressure) &&
            TouchFingerEqual(
                first.TouchpadFinger1Active, first.TouchpadFinger1X, first.TouchpadFinger1Y,
                first.TouchpadFinger1Id, first.TouchpadFinger1Pressure,
                second.TouchpadFinger1Active, second.TouchpadFinger1X, second.TouchpadFinger1Y,
                second.TouchpadFinger1Id, second.TouchpadFinger1Pressure);
    }

    private static bool TouchFingerEqual(
        bool firstActive, ushort firstX, ushort firstY, byte firstId, float firstPressure,
        bool secondActive, ushort secondX, ushort secondY, byte secondId, float secondPressure) =>
        firstActive == secondActive && firstX == secondX && firstY == secondY &&
        firstId == secondId && firstPressure == secondPressure;

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
        ControllerProfile? mappingProfile,
        string virtualGamepadType,
        string controllerName)
    {
        public HMContext Context { get; } = context;
        public HMController Controller { get; } = controller;
        public HMProfile OutputProfile { get; } = outputProfile;
        public ConnectedGamepad Device { get; set; } = device;
        public ControllerProfile? MappingProfile { get; set; } = mappingProfile;
        public string VirtualGamepadType { get; } = virtualGamepadType;
        public string ControllerName { get; } = controllerName;
        public HMGamepadState? LastSubmittedState { get; set; }
        public long LastRumbleEmitMs;
        public MotionSensorData? LastMotion { get; set; }
        public long LastMotionSubmitMs;
    }
}
