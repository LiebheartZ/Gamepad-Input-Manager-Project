using System.Diagnostics;
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
    private readonly Dictionary<string, long> _rumbleGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _rumblePendingStopGeneration = new(StringComparer.Ordinal);
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _rumbleStopTimers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _batteryProbeLastMs = new(StringComparer.Ordinal);
    private readonly HashSet<int> _touchpadInstanceIds = [];
    private const long BatteryProbeIntervalMs = 5000;
    private IReadOnlyList<ConnectedGamepad> _redundantInterfaces = [];

    public IReadOnlyList<ConnectedGamepad> RedundantInterfaces => _redundantInterfaces;

    public event Action? DeviceMembershipChanged;

    private const int SdlJoydeviceadded = 0x605;
    private const int SdlJoydeviceremoved = 0x606;
    private const int SdlControllerdeviceadded = 0x653;
    private const int SdlControllerdeviceremoved = 0x654;
    private readonly byte[] _sdlEventBuffer = new byte[128];
    private long _lastSdlStateUpdateTimestamp;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return Task.CompletedTask;
        }

        // SDL3 prefiere XInput/WGI para mandos Xbox y reporta rutas "XInput#N"
        // que no son interfaces HID: HidHide, la identificación de mandos
        // virtuales y la lectura HID de batería necesitan rutas HID reales.
        // Forzamos el driver HIDAPI para Xbox para recuperar el comportamiento
        // de SDL2 (rutas \\?\HID#...).
        NativeMethods.SDL_SetHint("SDL_JOYSTICK_HIDAPI_XBOX", "1");
        NativeMethods.SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
        NativeMethods.SDL_SetHint("SDL_XINPUT_ENABLED", "0");

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

    public void PumpDeviceEvents()
    {
        VerifyOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            return;
        }

        SDL.SDL_PumpEvents();
        bool membershipChanged = false;
        while (NativeMethods.SDL_PollEvent(_sdlEventBuffer) != 0)
        {
            int type = BitConverter.ToInt32(_sdlEventBuffer, 0);
            if (type is SdlJoydeviceadded or SdlJoydeviceremoved or
                SdlControllerdeviceadded or SdlControllerdeviceremoved)
            {
                membershipChanged = true;
            }
        }

        if (membershipChanged)
        {
            DeviceMembershipChanged?.Invoke();
        }
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
        UpdateSdlInputState();

        if (!TryParseDeviceKey(deviceKey, out int instanceId) ||
            !_openJoysticks.TryGetValue(instanceId, out OpenJoystick? openJoystick) ||
            NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) == 0)
        {
            return Task.FromResult<GamepadInputSnapshot?>(null);
        }

        GamepadInputSnapshot snapshot = openJoystick.GameControllerHandle != IntPtr.Zero
            ? ReadMappedInput(openJoystick.GameControllerHandle, openJoystick.Handle)
            : ReadRawInput(openJoystick.Handle);
        snapshot = snapshot with
        {
            BatteryLevel = openJoystick.Device.BatteryLevel,
            BatteryPercent = openJoystick.Device.BatteryPercent,
            BatteryCharging = openJoystick.Device.BatteryCharging,
            Touchpad = ReadTouchpadSafe(openJoystick, instanceId)
        };
        return Task.FromResult<GamepadInputSnapshot?>(snapshot);
    }

    /// <summary>
    /// Las actualizaciones globales de joystick/gamecontroller/sensor son
    /// idénticas para todos los mandos. Varias lecturas dentro de la misma
    /// iteración de sondeo llegan en microsegundos; coalescemos las
    /// actualizaciones a ~1 ms para no repetir trabajo nativo por dispositivo.
    /// </summary>
    private void UpdateSdlInputState()
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastSdlStateUpdateTimestamp < Stopwatch.Frequency / 1000)
        {
            return;
        }

        _lastSdlStateUpdateTimestamp = now;
        NativeMethods.SDL_JoystickUpdate();
        NativeMethods.SDL_GameControllerUpdate();
        NativeMethods.SDL_SensorUpdate();
    }

    private TouchpadState? ReadTouchpadSafe(OpenJoystick openJoystick, int instanceId)
    {
        try
        {
            return ReadTouchpad(openJoystick, instanceId);
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or
            DllNotFoundException or
            InvalidOperationException)
        {
            return null;
        }
    }

    public Task<bool> RumbleAsync(
        string deviceKey,
        int lowMotor,
        int highMotor,
        int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        VerifyOwnerThread();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized ||
            !TryParseDeviceKey(deviceKey, out int instanceId) ||
            !_openJoysticks.TryGetValue(instanceId, out OpenJoystick? openJoystick) ||
            NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) == 0)
        {
            return Task.FromResult(false);
        }

        bool sent = TrySendRumble(deviceKey, lowMotor, highMotor, durationMilliseconds);

        if (sent && durationMilliseconds > 0)
        {
            ScheduleRumbleStop(deviceKey, durationMilliseconds);
        }

        return Task.FromResult(sent);
    }

    private bool TrySendRumble(
        string deviceKey,
        int lowMotor,
        int highMotor,
        int durationMilliseconds)
    {
        if (!TryParseDeviceKey(deviceKey, out int instanceId) ||
            !_openJoysticks.TryGetValue(instanceId, out OpenJoystick? openJoystick) ||
            NativeMethods.SDL_JoystickGetAttached(openJoystick.Handle) == 0)
        {
            return false;
        }

        ushort low = (ushort)Math.Clamp(lowMotor, 0, ushort.MaxValue);
        ushort high = (ushort)Math.Clamp(highMotor, 0, ushort.MaxValue);
        uint duration = (uint)Math.Clamp(durationMilliseconds, 0, int.MaxValue);
        bool sent = false;
        if (openJoystick.GameControllerHandle != IntPtr.Zero &&
            NativeMethods.SDL_GameControllerHasRumble(openJoystick.GameControllerHandle) == 1)
        {
            sent = NativeMethods.SDL_GameControllerRumble(
                openJoystick.GameControllerHandle, low, high, duration) == 0;
            if (!sent &&
                NativeMethods.SDL_GameControllerHasRumbleTriggers(openJoystick.GameControllerHandle) == 1)
            {
                sent = NativeMethods.SDL_GameControllerRumbleTriggers(
                    openJoystick.GameControllerHandle, (short)low, (short)high, duration) == 0;
            }
        }

        if (!sent && NativeMethods.SDL_JoystickHasRumble(openJoystick.Handle) == 1)
        {
            sent = NativeMethods.SDL_JoystickRumble(
                openJoystick.Handle, low, high, duration) == 0;
        }

        return sent;
    }

    private void ScheduleRumbleStop(string deviceKey, int durationMilliseconds)
    {
        long generation = _rumbleGenerations.TryGetValue(deviceKey, out long current)
            ? current + 1
            : 1;
        _rumbleGenerations[deviceKey] = generation;
        _rumblePendingStopGeneration[deviceKey] = generation;

        // Un solo timer por dispositivo: se reinicia con cada vibración en
        // lugar de crear un DispatcherTimer nuevo por llamada.
        if (!_rumbleStopTimers.TryGetValue(deviceKey, out System.Windows.Threading.DispatcherTimer? timer))
        {
            timer = new System.Windows.Threading.DispatcherTimer();
            timer.Tick += (_, _) => OnRumbleStopTimerTick(deviceKey);
            _rumbleStopTimers[deviceKey] = timer;
        }

        timer.Interval = TimeSpan.FromMilliseconds(durationMilliseconds);
        timer.Stop();
        timer.Start();
    }

    private void OnRumbleStopTimerTick(string deviceKey)
    {
        if (_rumbleStopTimers.TryGetValue(deviceKey, out System.Windows.Threading.DispatcherTimer? timer))
        {
            timer.Stop();
        }

        long pending = _rumblePendingStopGeneration.GetValueOrDefault(deviceKey);
        if (_rumbleGenerations.TryGetValue(deviceKey, out long active) && active == pending)
        {
            _rumbleGenerations.Remove(deviceKey);
            _rumblePendingStopGeneration.Remove(deviceKey);
            _rumbleStopTimers.Remove(deviceKey);
            TrySendRumble(deviceKey, 0, 0, 0);
        }
        else if (!_rumbleGenerations.ContainsKey(deviceKey))
        {
            _rumblePendingStopGeneration.Remove(deviceKey);
            _rumbleStopTimers.Remove(deviceKey);
        }
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
            CleanupDeviceState($"SDL2:{instanceId}", instanceId);
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
            string? devicePath = GetJoystickPath(joystick);
            var device = new ConnectedGamepad
            {
                DeviceKey = $"SDL2:{openedInstanceId}",
                Name = SDL.SDL_JoystickName(joystick) ?? "Mando desconocido",
                VendorId = vendorId == 0 ? null : vendorId,
                ProductId = productId == 0 ? null : productId,
                SerialNumber = GetOptionalString(joystick, NativeMethods.SDL_JoystickGetSerial),
                DevicePath = devicePath,
                SdlGuid = GetSdlGuid(joystick),
                PhysicalAxisCount = Math.Max(0, NativeMethods.SDL_JoystickNumAxes(joystick)),
                PhysicalButtonCount = Math.Max(0, NativeMethods.SDL_JoystickNumButtons(joystick)),
                IsVirtualGamepad = HidMaestroDeviceIdentifier.IsVirtualGamepad(devicePath),
                ConnectionTransport = DetectConnectionTransport(devicePath, vendorId, productId),
                BatteryLevel = ReadBatteryLevel(joystick)
            };
            IntPtr gameController = NativeMethods.SDL_IsGameController(index) == 1
                ? NativeMethods.SDL_GameControllerOpen(index)
                : IntPtr.Zero;
            device = device with { HasStandardMapping = gameController != IntPtr.Zero };
            if (gameController != IntPtr.Zero)
            {
                EnableSensor(gameController, NativeMethods.SDL_SENSOR_ACCEL);
                EnableSensor(gameController, NativeMethods.SDL_SENSOR_GYRO);
            }

            _openJoysticks.Add(
                openedInstanceId,
                new OpenJoystick(joystick, gameController, device));
        }

        RefreshBatteryReadings();

        ConnectedGamepad[] devices = _openJoysticks
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Device)
            .ToArray();
        (IReadOnlyList<ConnectedGamepad> kept, IReadOnlyList<ConnectedGamepad> redundant) =
            FilterRedundantInterfaces(devices);
        _redundantInterfaces = redundant;
        return kept;
    }

    private static (IReadOnlyList<ConnectedGamepad> Kept, IReadOnlyList<ConnectedGamepad> Redundant)
        FilterRedundantInterfaces(IReadOnlyList<ConnectedGamepad> devices)
    {
        var redundant = new List<ConnectedGamepad>();
        var result = devices.ToList();
        foreach (IGrouping<(string Name, ushort? Vendor, ushort? Product), ConnectedGamepad> group in devices
            .Where(device => !device.IsVirtualGamepad)
            .GroupBy(device => (device.Name, device.VendorId, device.ProductId)))
        {
            ConnectedGamepad[] members = group.ToArray();
            if (members.Length < 2)
            {
                continue;
            }

            bool anyMapped = members.Any(member => member.HasStandardMapping);
            int bestScore = members.Max(member =>
                member.PhysicalAxisCount + member.PhysicalButtonCount);
            int bestAxes = members.Max(member => member.PhysicalAxisCount);
            foreach (ConnectedGamepad member in members)
            {
                int score = member.PhysicalAxisCount + member.PhysicalButtonCount;
                bool isRedundant = member.PhysicalAxisCount == 0 && bestAxes > 0 ||
                    score == 0 && bestScore > 0 ||
                    anyMapped && !member.HasStandardMapping && score < bestScore;
                if (isRedundant)
                {
                    result.Remove(member);
                    redundant.Add(member with { IsRedundantInterface = true });
                }
            }
        }

        return (result, redundant);
    }

    private void CleanupDeviceState(string deviceKey, int instanceId)
    {
        _rumbleGenerations.Remove(deviceKey);
        _rumblePendingStopGeneration.Remove(deviceKey);
        if (_rumbleStopTimers.Remove(deviceKey, out System.Windows.Threading.DispatcherTimer? timer))
        {
            timer.Stop();
        }

        _batteryProbeLastMs.Remove(deviceKey);
        _touchpadInstanceIds.Remove(instanceId);
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
            axes.Add(new GamepadAxisState(name, normalizedValue));
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
            PhysicalButtonCount = Math.Max(0, NativeMethods.SDL_JoystickNumButtons(joystick)),
            Motion = ReadMotion(gameController),
            BatteryLevel = ReadBatteryLevel(joystick)
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
            axes.Add(new GamepadAxisState($"Eje {axis}", normalizedValue));
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
            PhysicalButtonCount = buttonCount,
            BatteryLevel = ReadBatteryLevel(joystick)
        };
    }

    private TouchpadState? ReadTouchpad(OpenJoystick openJoystick, int instanceId)
    {
        var fingers = new List<TouchpadFingerState>(2);
        var diagnostics = new StringBuilder();
        bool hasTouchpad;

        if (openJoystick.GameControllerHandle != IntPtr.Zero)
        {
            int touchpads = NativeMethods.SDL_GameControllerGetNumTouchpads(
                openJoystick.GameControllerHandle);
            diagnostics.Append($"touchpads={touchpads}");
            for (int touchpad = 0; touchpad < touchpads && fingers.Count < 2; touchpad++)
            {
                int count = NativeMethods.SDL_GameControllerGetNumTouchpadFingers(
                    openJoystick.GameControllerHandle, touchpad);
                diagnostics.Append($" tp{touchpad}.slots={count}");
                for (int i = 0; i < count && fingers.Count < 2; i++)
                {
                    int result = NativeMethods.SDL_GameControllerGetTouchpadFinger(
                        openJoystick.GameControllerHandle, touchpad, i,
                        out byte state, out float x, out float y, out float pressure);
                    diagnostics.Append($" f{i}[r={result},s={state},x={x:F2},y={y:F2},p={pressure:F2}]");
                    if (result == 0 && state != 0)
                    {
                        fingers.Add(new TouchpadFingerState
                        {
                            Active = true,
                            X = Math.Clamp(x, 0f, 1f),
                            Y = Math.Clamp(y, 0f, 1f),
                            Id = i,
                            Pressure = Math.Clamp(pressure, 0f, 1f)
                        });
                    }
                }
            }

            if (fingers.Count > 0)
            {
                hasTouchpad = true;
            }
            else
            {
                int type = NativeMethods.SDL_GameControllerGetType(openJoystick.GameControllerHandle);
                hasTouchpad = type is 4 or 7 ||
                    (type == 0 && openJoystick.Device.VendorId == 0x054C);
            }
        }
        else
        {
            long touchId = FindTrackpadTouchDevice();
            if (touchId != 0)
            {
                int count = NativeMethods.SDL_GetNumTouchFingers(touchId);
                for (int i = 0; i < count && fingers.Count < 2; i++)
                {
                    IntPtr fingerPtr = NativeMethods.SDL_GetTouchFinger(touchId, i);
                    if (fingerPtr != IntPtr.Zero)
                    {
                        fingers.Add(ToTouchpadFinger(
                            Marshal.PtrToStructure<NativeMethods.SdlFinger>(fingerPtr)));
                    }
                }
            }

            if (fingers.Count > 0)
            {
                hasTouchpad = true;
                _touchpadInstanceIds.Add(instanceId);
            }
            else
            {
                hasTouchpad = _touchpadInstanceIds.Contains(instanceId);
            }
        }

        return new TouchpadState
        {
            HasTouchpad = hasTouchpad,
            Finger0 = fingers.Count > 0 ? fingers[0] : null,
            Finger1 = fingers.Count > 1 ? fingers[1] : null,
            Diagnostics = diagnostics.Length > 0 ? diagnostics.ToString() : null
        };
    }

    private static long FindTrackpadTouchDevice()
    {
        long found = 0;
        int count = 0;
        int devices = NativeMethods.SDL_GetNumTouchDevices();
        for (int i = 0; i < devices; i++)
        {
            long touchId = NativeMethods.SDL_GetTouchDevice(i);
            if (touchId == 0 || NativeMethods.SDL_GetTouchDeviceType(touchId) != 1)
            {
                continue;
            }

            found = touchId;
            count++;
        }

        return count == 1 ? found : 0;
    }

    private static TouchpadFingerState ToTouchpadFinger(in NativeMethods.SdlFinger finger) => new()
    {
        Active = true,
        X = Math.Clamp(finger.X, 0d, 1d),
        Y = Math.Clamp(finger.Y, 0d, 1d),
        Id = (int)finger.Id,
        Pressure = Math.Clamp(finger.Pressure, 0f, 1f)
    };

    private void RefreshBatteryReadings()
    {
        long now = Environment.TickCount64;
        int xinputDeviceCount = _openJoysticks.Values.Count(o => o.Device.VendorId == 0x045E);
        List<int>? xinputSlots = null;

        foreach (KeyValuePair<int, OpenJoystick> pair in _openJoysticks.ToArray())
        {
            OpenJoystick openJoystick = pair.Value;
            string key = openJoystick.Device.DeviceKey;
            if (_batteryProbeLastMs.TryGetValue(key, out long last) && now - last < BatteryProbeIntervalMs)
            {
                continue;
            }

            _batteryProbeLastMs[key] = now;

            int xinputUserIndex = -1;
            if (openJoystick.Device.VendorId == 0x045E)
            {
                try
                {
                    xinputUserIndex = NativeMethods.SDL_JoystickGetPlayerIndex(openJoystick.Handle);
                }
                catch
                {
                    xinputUserIndex = -1;
                }

                if (xinputUserIndex < 0)
                {
                    xinputSlots ??= GamepadBatteryProbe.GetConnectedXInputUserIndices();
                    if (xinputSlots.Count == 1 && xinputDeviceCount == 1)
                    {
                        xinputUserIndex = xinputSlots[0];
                    }
                }
            }

            BatteryReading reading = GamepadBatteryProbe.Probe(openJoystick.Handle, openJoystick.Device, xinputUserIndex);
            ConnectedGamepad updated = openJoystick.Device with
            {
                BatteryLevel = reading.Level,
                BatteryPercent = reading.Percent,
                BatteryCharging = reading.Charging
            };
            _openJoysticks[pair.Key] = openJoystick with { Device = updated };
        }
    }
    private static GamepadBatteryLevel ReadBatteryLevel(IntPtr joystick)
    {
        try
        {
            return GamepadBatteryProbe.MapSdlPowerLevel(NativeMethods.SDL_JoystickCurrentPowerLevel(joystick));

        }
        catch (EntryPointNotFoundException)
        {
            return GamepadBatteryLevel.Unknown;
        }
    }

    internal static GamepadConnectionTransport DetectConnectionTransport(
        string? devicePath,
        ushort? vendorId,
        ushort? productId)
    {
        return DeviceTransportResolver.Resolve(devicePath, vendorId ?? 0, productId ?? 0);
    }
    private static MotionSensorData? ReadMotion(IntPtr gameController)
    {
        try
        {
            bool hasAccelerometer = NativeMethods.SDL_GameControllerHasSensor(
                gameController, NativeMethods.SDL_SENSOR_ACCEL) == 1;
            bool hasGyroscope = NativeMethods.SDL_GameControllerHasSensor(
                gameController, NativeMethods.SDL_SENSOR_GYRO) == 1;
            if (!hasAccelerometer && !hasGyroscope)
            {
                return null;
            }

            bool accelEnabled = IsSensorEnabled(gameController, NativeMethods.SDL_SENSOR_ACCEL);
            bool gyroEnabled = IsSensorEnabled(gameController, NativeMethods.SDL_SENSOR_GYRO);
            if (hasAccelerometer && !accelEnabled)
            {
                EnableSensor(gameController, NativeMethods.SDL_SENSOR_ACCEL);
                accelEnabled = IsSensorEnabled(gameController, NativeMethods.SDL_SENSOR_ACCEL);
            }

            if (hasGyroscope && !gyroEnabled)
            {
                EnableSensor(gameController, NativeMethods.SDL_SENSOR_GYRO);
                gyroEnabled = IsSensorEnabled(gameController, NativeMethods.SDL_SENSOR_GYRO);
            }

            var accel = new float[3];
            var gyro = new float[3];
            bool accelOk = hasAccelerometer && accelEnabled &&
                NativeMethods.SDL_GameControllerGetSensorData(
                    gameController, NativeMethods.SDL_SENSOR_ACCEL, accel, accel.Length) == 0;
            bool gyroOk = hasGyroscope && gyroEnabled &&
                NativeMethods.SDL_GameControllerGetSensorData(
                    gameController, NativeMethods.SDL_SENSOR_GYRO, gyro, gyro.Length) == 0;
            if (!accelOk && !gyroOk)
            {
                return new MotionSensorData
                {
                    HasAccelerometer = hasAccelerometer,
                    HasGyroscope = hasGyroscope,
                    AccelerometerEnabled = accelEnabled,
                    GyroscopeEnabled = gyroEnabled
                };
            }

            return new MotionSensorData
            {
                HasAccelerometer = hasAccelerometer && accelOk,
                HasGyroscope = hasGyroscope && gyroOk,
                AccelerometerEnabled = accelEnabled,
                GyroscopeEnabled = gyroEnabled,
                AccelXMs2 = accelOk ? accel[0] : 0d,
                AccelYMs2 = accelOk ? accel[1] : 0d,
                AccelZMs2 = accelOk ? accel[2] : 0d,
                GyroPitchRadS = gyroOk ? gyro[0] : 0d,
                GyroYawRadS = gyroOk ? gyro[1] : 0d,
                GyroRollRadS = gyroOk ? gyro[2] : 0d
            };
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool IsSensorEnabled(IntPtr gameController, int sensorType)
    {
        try
        {
            return NativeMethods.SDL_GameControllerIsSensorEnabled(gameController, sensorType) == 1;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool EnableSensor(IntPtr gameController, int sensorType)
    {
        try
        {
            return NativeMethods.SDL_GameControllerHasSensor(gameController, sensorType) == 1 &&
                NativeMethods.SDL_GameControllerSetSensorEnabled(gameController, sensorType, 1) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
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

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SDL_SetHint")]
        internal static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_PollEvent([Out] byte[] events);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_JoystickUpdate();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickGetDeviceInstanceID(int deviceIndex);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickGetAttached(IntPtr joystick);
        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickGetPlayerIndex")]
        internal static extern int SDL_JoystickGetPlayerIndex(IntPtr joystick);

        [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickCurrentPowerLevel")]
        internal static extern int SDL_JoystickCurrentPowerLevel(IntPtr joystick);

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

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerHasRumble(IntPtr gameController);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerRumble(
            IntPtr gameController, ushort lowFrequencyRumble, ushort highFrequencyRumble, uint durationMs);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerHasRumbleTriggers(IntPtr gameController);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerRumbleTriggers(
            IntPtr gameController, short leftTrigger, short rightTrigger, uint durationMs);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickHasRumble(IntPtr joystick);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_JoystickRumble(
            IntPtr joystick, ushort lowFrequencyRumble, ushort highFrequencyRumble, uint durationMs);

        internal const int SDL_SENSOR_ACCEL = 1;
        internal const int SDL_SENSOR_GYRO = 2;

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SDL_SensorUpdate();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerHasSensor(IntPtr gameController, int type);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerIsSensorEnabled(IntPtr gameController, int type);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerSetSensorEnabled(IntPtr gameController, int type, int enabled);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerGetSensorData(
            IntPtr gameController, int type, [Out] float[] data, int numValues);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerGetSensorDataWithTimestamp(
            IntPtr gameController, int type, [Out] float[] data, int numValues, out ulong timestamp);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerGetType(IntPtr gameController);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerGetNumTouchpads(IntPtr gameController);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GameControllerGetNumTouchpadFingers(
            IntPtr gameController, int touchpad);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "SDL_GameControllerGetTouchpadFinger")]
        internal static extern int SDL_GameControllerGetTouchpadFinger(
            IntPtr gameController, int touchpad, int finger,
            out byte state, out float x, out float y, out float pressure);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GetNumTouchDevices();

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long SDL_GetTouchDevice(int index);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GetTouchDeviceType(long touchID);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SDL_GetNumTouchFingers(long touchID);

        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr SDL_GetTouchFinger(long touchID, int index);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SdlFinger
        {
            public long Id;
            public float X;
            public float Y;
            public float Pressure;
        }
    }

    private sealed record OpenJoystick(
        IntPtr Handle,
        IntPtr GameControllerHandle,
        ConnectedGamepad Device);
}
