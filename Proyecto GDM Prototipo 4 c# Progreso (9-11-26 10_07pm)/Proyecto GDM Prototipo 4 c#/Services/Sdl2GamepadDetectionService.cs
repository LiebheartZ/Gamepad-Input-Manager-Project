using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using GDM.Models;
using GDM.Services.Abstractions;
using SDL2;

namespace GDM.Services;

public sealed class Sdl2GamepadDetectionService : IGamepadDetectionService, IGamepadInputService
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<int, OpenJoystick> _openJoysticks = [];
    private bool _initialized;
    private bool _disposed;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return Task.CompletedTask;
        }

        if (SDL.SDL_Init(SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER) < 0)
        {
            throw new InvalidOperationException(
                $"SDL2 no pudo inicializar la detección de mandos: {SDL.SDL_GetError()}");
        }

        _initialized = true;
        NativeMethods.SDL_JoystickEventState(NativeMethods.SDL_ENABLE);
        SDL.SDL_PumpEvents();
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            PumpEvents();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConnectedGamepad>> EnumerateConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("SDL2 aún no está inicializado.");
        }

        PumpEvents();
        return Task.FromResult(UpdateDeviceList(cancellationToken));
    }

    public Task<GamepadInputSnapshot?> ReadInputAsync(
        string deviceKey,
        CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("SDL2 aún no está inicializado.");
        }

        PumpEvents();
        NativeMethods.SDL_JoystickUpdate();
        NativeMethods.SDL_GameControllerUpdate();

        if (!TryParseDeviceKey(deviceKey, out int instanceId) ||
            !_openJoysticks.TryGetValue(instanceId, out OpenJoystick? openJoystick) ||
            NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) == 0)
        {
            return Task.FromResult<GamepadInputSnapshot?>(null);
        }

        GamepadInputSnapshot snapshot = openJoystick.GameControllerHandle != IntPtr.Zero
            ? ReadMappedInput(openJoystick.GameControllerHandle, openJoystick.Handle)
            : ReadRawInput(openJoystick.Handle);
        return Task.FromResult<GamepadInputSnapshot?>(snapshot);
    }

    public void Dispose()
    {
        VerifyOwnerThread();
        if (_disposed)
        {
            return;
        }

        if (_initialized)
        {
            foreach (OpenJoystick openJoystick in _openJoysticks.Values)
            {
                CloseJoystick(openJoystick);
            }

            _openJoysticks.Clear();
            SDL.SDL_Quit();
            _initialized = false;
        }

        _disposed = true;
    }

    private IReadOnlyList<ConnectedGamepad> UpdateDeviceList(CancellationToken cancellationToken)
    {
        NativeMethods.SDL_JoystickUpdate();

        int count = SDL.SDL_NumJoysticks();
        var currentInstanceIds = new HashSet<int>();
        for (int index = 0; index < count; index++)
        {
            int instanceId = NativeMethods.SDL_JoystickGetDeviceInstanceID(index);
            if (instanceId >= 0)
            {
                currentInstanceIds.Add(instanceId);
            }
        }

        foreach ((int instanceId, OpenJoystick openJoystick) in _openJoysticks.ToArray())
        {
            if (currentInstanceIds.Contains(instanceId) &&
                NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) != 0)
            {
                continue;
            }

            CloseJoystick(openJoystick);
            _openJoysticks.Remove(instanceId);
        }

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int instanceId = NativeMethods.SDL_JoystickGetDeviceInstanceID(index);
            if (instanceId < 0 || _openJoysticks.ContainsKey(instanceId))
            {
                continue;
            }

            IntPtr joystick = SDL.SDL_JoystickOpen(index);
            if (joystick == IntPtr.Zero)
            {
                continue;
            }

            int openedInstanceId = NativeMethods.SDL_JoystickInstanceID(joystick);
            if (openedInstanceId < 0 ||
                openedInstanceId != instanceId ||
                NativeMethods.SDL_JoystickGetAttached(joystick) == 0)
            {
                SDL.SDL_JoystickClose(joystick);
                continue;
            }

            ushort vendorId = SDL.SDL_JoystickGetVendor(joystick);
            ushort productId = SDL.SDL_JoystickGetProduct(joystick);
            var device = new ConnectedGamepad
            {
                DeviceKey = $"SDL2:{openedInstanceId}",
                Name = SDL.SDL_JoystickName(joystick) ?? "Mando desconocido",
                VendorId = vendorId == 0 ? null : vendorId,
                ProductId = productId == 0 ? null : productId,
                SerialNumber = GetOptionalString(joystick, NativeMethods.SDL_JoystickGetSerial),
                DevicePath = GetJoystickPath(joystick),
                SdlGuid = GetSdlGuid(joystick)
            };
            IntPtr gameController = NativeMethods.SDL_IsGameController(index) == 1
                ? NativeMethods.SDL_GameControllerOpen(index)
                : IntPtr.Zero;
            _openJoysticks.Add(
                openedInstanceId,
                new OpenJoystick(joystick, gameController, device));
        }

        return _openJoysticks
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Device)
            .ToArray();
    }

    private static void CloseJoystick(OpenJoystick openJoystick)
    {
        if (openJoystick.GameControllerHandle != IntPtr.Zero)
        {
            NativeMethods.SDL_GameControllerClose(openJoystick.GameControllerHandle);
        }

        SDL.SDL_JoystickClose(openJoystick.Handle);
    }

    private static GamepadInputSnapshot ReadMappedInput(IntPtr gameController, IntPtr joystick)
    {
        (int Index, string Name)[] axisNames =
        [
            (0, "Stick izq. X"),
            (1, "Stick izq. Y"),
            (2, "Stick der. X"),
            (3, "Stick der. Y"),
            (4, "Gatillo izq."),
            (5, "Gatillo der.")
        ];
        var axes = new List<GamepadAxisState>(axisNames.Length);
        foreach ((int axis, string name) in axisNames)
        {
            if (NativeMethods.SDL_GameControllerHasAxis(gameController, axis) == 0)
            {
                continue;
            }

            short rawValue = NativeMethods.SDL_GameControllerGetAxis(gameController, axis);
            double normalizedValue = rawValue / 32767d;
            normalizedValue = Math.Clamp(normalizedValue, axis >= 4 ? 0d : -1d, 1d);
            axes.Add(new GamepadAxisState(
                name,
                normalizedValue,
                normalizedValue.ToString("0.000", CultureInfo.InvariantCulture)));
        }

        (int Index, string Name)[] buttonNames =
        [
            (0, "A / Cruz"),
            (1, "B / Círculo"),
            (2, "X / Cuadrado"),
            (3, "Y / Triángulo"),
            (4, "Atrás / Share"),
            (5, "Guía / PS"),
            (6, "Inicio / Options"),
            (7, "Stick izq. (L3)"),
            (8, "Stick der. (R3)"),
            (9, "Hombro izq."),
            (10, "Hombro der."),
            (11, "Cruceta arriba"),
            (12, "Cruceta abajo"),
            (13, "Cruceta izquierda"),
            (14, "Cruceta derecha"),
            (15, "Botón extra (Misc 1)"),
            (16, "Pala 1"),
            (17, "Pala 2"),
            (18, "Pala 3"),
            (19, "Pala 4"),
            (20, "Panel táctil (clic)")
        ];
        var buttons = new List<GamepadButtonState>(buttonNames.Length);
        foreach ((int button, string name) in buttonNames)
        {
            if (NativeMethods.SDL_GameControllerHasButton(gameController, button) == 0)
            {
                continue;
            }

            buttons.Add(new GamepadButtonState(
                name,
                NativeMethods.SDL_GameControllerGetButton(gameController, button) != 0));
        }

        return new GamepadInputSnapshot
        {
            IsStandardGameController = true,
            Axes = axes,
            Buttons = buttons,
            PhysicalAxisCount = Math.Max(0, NativeMethods.SDL_JoystickNumAxes(joystick)),
            PhysicalButtonCount = Math.Max(0, NativeMethods.SDL_JoystickNumButtons(joystick))
        };
    }

    private static GamepadInputSnapshot ReadRawInput(IntPtr joystick)
    {
        int axisCount = Math.Max(0, NativeMethods.SDL_JoystickNumAxes(joystick));
        int buttonCount = Math.Max(0, NativeMethods.SDL_JoystickNumButtons(joystick));
        var axes = new List<GamepadAxisState>(axisCount);
        for (int axis = 0; axis < axisCount; axis++)
        {
            short rawValue = NativeMethods.SDL_JoystickGetAxis(joystick, axis);
            double normalizedValue = Math.Clamp(rawValue / 32767d, -1d, 1d);
            axes.Add(new GamepadAxisState(
                $"Eje {axis}",
                normalizedValue,
                normalizedValue.ToString("0.000", CultureInfo.InvariantCulture)));
        }

        var buttons = new List<GamepadButtonState>(buttonCount);
        for (int button = 0; button < buttonCount; button++)
        {
            buttons.Add(new GamepadButtonState(
                $"Botón {button}",
                NativeMethods.SDL_JoystickGetButton(joystick, button) != 0));
        }

        return new GamepadInputSnapshot
        {
            IsStandardGameController = false,
            Axes = axes,
            Buttons = buttons,
            PhysicalAxisCount = axisCount,
            PhysicalButtonCount = buttonCount
        };
    }

    private static bool TryParseDeviceKey(string deviceKey, out int instanceId)
    {
        const string prefix = "SDL2:";
        instanceId = -1;
        return deviceKey.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(deviceKey.AsSpan(prefix.Length), out instanceId);
    }

    private void PumpEvents()
    {
        VerifyOwnerThread();
        SDL.SDL_PumpEvents();
    }

    private void VerifyOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "Las operaciones de SDL2 deben ejecutarse en el hilo que creó el servicio.");
        }
    }

    private static string? GetOptionalString(IntPtr joystick, Func<IntPtr, IntPtr> getter)
    {
        try
        {
            IntPtr value = getter(joystick);
            string? text = value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? GetJoystickPath(IntPtr joystick)
    {
        IntPtr value = NativeMethods.SDL_JoystickPath(joystick);
        string? text = value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? GetSdlGuid(IntPtr joystick)
    {
        var buffer = new StringBuilder(33);
        NativeMethods.SDL_JoystickGetGUIDString(joystick, buffer, buffer.Capacity);
        string value = buffer.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static class NativeMethods
    {
        internal const int SDL_ENABLE = 1;

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickEventState(int state);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_JoystickUpdate();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickGetDeviceInstanceID(int deviceIndex);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickGetAttached(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickInstanceID(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SDL_JoystickGetSerial(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SDL_JoystickPath(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_JoystickGetGUIDString(
            IntPtr joystick,
            StringBuilder pszGUID,
            int cbGUID);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickNumAxes(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickNumButtons(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern short SDL_JoystickGetAxis(IntPtr joystick, int axis);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern byte SDL_JoystickGetButton(IntPtr joystick, int button);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_GameControllerUpdate();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_IsGameController(int joystickIndex);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SDL_GameControllerOpen(int joystickIndex);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_GameControllerClose(IntPtr gameController);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern short SDL_GameControllerGetAxis(IntPtr gameController, int axis);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern byte SDL_GameControllerGetButton(IntPtr gameController, int button);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerHasAxis(IntPtr gameController, int axis);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerHasButton(IntPtr gameController, int button);
    }

    private sealed record OpenJoystick(
        IntPtr Handle,
        IntPtr GameControllerHandle,
        ConnectedGamepad Device);
}
