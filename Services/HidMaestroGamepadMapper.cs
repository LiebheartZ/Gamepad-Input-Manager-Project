using System.Globalization;
using GDM.Models;
using HIDMaestro;

namespace GDM.Services;

public static class HidMaestroGamepadMapper
{
    public static HMGamepadState CreateState(
        GamepadInputSnapshot snapshot,
        HMProfile profile,
        ControllerProfile? mappingProfile = null,
        string? virtualGamepadType = null,
        IReadOnlyCollection<string>? additionalTargetButtons = null,
        MotionSensorData? motion = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        if (!snapshot.IsStandardGameController)
        {
            throw new InvalidOperationException(
                "SDL no tiene un mapeo estándar para este mando; no se pueden transferir sus controles con seguridad.");
        }

        var targetButtons = new HashSet<string>(StringComparer.Ordinal);
        foreach (GamepadControlOption source in GamepadControlCatalog.Buttons)
        {
            if (source.InputName is null || !IsPressed(snapshot, source.InputName))
            {
                continue;
            }

            string target = mappingProfile?.ButtonMappings.TryGetValue(
                source.Id,
                out string? mappedTarget) == true
                ? NormalizeButtonTarget(mappedTarget, source.Id)
                : source.Id;
            if (!string.Equals(target, GamepadControlCatalog.NoButtonTarget.Id, StringComparison.Ordinal) &&
                GamepadControlCatalog.IsButtonTargetSupported(target, virtualGamepadType))
            {
                targetButtons.Add(target);
            }
        }

        if (additionalTargetButtons is not null)
        {
            foreach (string target in additionalTargetButtons)
            {
                if (!string.Equals(target, GamepadControlCatalog.NoButtonTarget.Id, StringComparison.Ordinal) &&
                    GamepadControlCatalog.Buttons.Any(button => button.Id == target) &&
                    GamepadControlCatalog.IsButtonTargetSupported(target, virtualGamepadType))
                {
                    targetButtons.Add(target);
                }
            }
        }

        HMButton buttons = HMButton.None;
        AddButton("south", HMButton.A);
        AddButton("east", HMButton.B);
        AddButton("west", HMButton.X);
        AddButton("north", HMButton.Y);
        AddButton("leftBumper", HMButton.LeftBumper);
        AddButton("rightBumper", HMButton.RightBumper);
        AddButton("back", HMButton.Back);
        AddButton("start", HMButton.Start);
        AddButton("guide", HMButton.Guide);
        AddButton("leftStick", HMButton.LeftStick);
        AddButton("rightStick", HMButton.RightStick);
        AddButton("touchpad", HMButton.Touchpad);
        AddButton("misc1", HMButton.Misc1);
        AddButton("paddle1", HMButton.RightPaddle);
        AddButton("paddle2", HMButton.LeftPaddle);
        AddButton("paddle3", HMButton.RightPaddle2);
        AddButton("paddle4", HMButton.LeftPaddle2);

        double leftX = GetAxis(snapshot, "Stick izq. X");
        double leftY = GetAxis(snapshot, "Stick izq. Y");
        double rightX = GetAxis(snapshot, "Stick der. X");
        double rightY = GetAxis(snapshot, "Stick der. Y");
        double leftTrigger = GetAxis(snapshot, "Gatillo izq.");
        double rightTrigger = GetAxis(snapshot, "Gatillo der.");
        if (mappingProfile?.AxisInversions.GetValueOrDefault("leftX") == true)
        {
            leftX = -leftX;
        }

        if (mappingProfile?.AxisInversions.GetValueOrDefault("leftY") == true)
        {
            leftY = -leftY;
        }

        if (mappingProfile?.AxisInversions.GetValueOrDefault("rightX") == true)
        {
            rightX = -rightX;
        }

        if (mappingProfile?.AxisInversions.GetValueOrDefault("rightY") == true)
        {
            rightY = -rightY;
        }

        if (mappingProfile?.AxisInversions.GetValueOrDefault("leftTrigger") == true)
        {
            leftTrigger = 1d - leftTrigger;
        }

        if (mappingProfile?.AxisInversions.GetValueOrDefault("rightTrigger") == true)
        {
            rightTrigger = 1d - rightTrigger;
        }

        if (mappingProfile?.DeadZones.TryGetValue("leftStick", out double leftDeadZone) == true)
        {
            (leftX, leftY) = ApplyDeadZone(leftX, leftY, leftDeadZone);
        }

        if (mappingProfile?.DeadZones.TryGetValue("rightStick", out double rightDeadZone) == true)
        {
            (rightX, rightY) = ApplyDeadZone(rightX, rightY, rightDeadZone);
        }

        var sourceAxes = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["leftX"] = leftX,
            ["leftY"] = leftY,
            ["rightX"] = rightX,
            ["rightY"] = rightY,
            ["leftTrigger"] = leftTrigger,
            ["rightTrigger"] = rightTrigger
        };
        var targetAxes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string source, double value) in sourceAxes)
        {
            string target = mappingProfile?.AxisMappings.TryGetValue(
                source,
                out string? mappedTarget) == true
                ? NormalizeAxisTarget(mappedTarget, source)
                : source;

            if (string.Equals(target, GamepadControlCatalog.NoButtonTarget.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (!targetAxes.TryGetValue(target, out double current) ||
                Math.Abs(value) > Math.Abs(current))
            {
                targetAxes[target] = value;
            }
        }

        Dictionary<HMAxis, float> axes = HMGamepadStateHelpers.StandardAxes(
            profile,
            leftStickX: ToUnsignedStickValue(GetMappedAxis("leftX")),
            leftStickY: ToUnsignedStickValue(GetMappedAxis("leftY")),
            rightStickX: ToUnsignedStickValue(GetMappedAxis("rightX")),
            rightStickY: ToUnsignedStickValue(GetMappedAxis("rightY")),
            leftTrigger: ToTriggerValue(GetMappedAxis("leftTrigger")),
            rightTrigger: ToTriggerValue(GetMappedAxis("rightTrigger")));

        HMGamepadState state = new()
        {
            Axes = axes,
            Buttons = buttons,
            Hat = GetHat()
        };
        ApplyMotion(ref state, motion);
        ApplyBattery(ref state, snapshot);
        ApplyTouchpad(ref state, snapshot.Touchpad);
        return state;

        void AddButton(string name, HMButton target)
        {
            if (targetButtons.Contains(name))
            {
                buttons |= target;
            }
        }

        double GetMappedAxis(string target) =>
            targetAxes.TryGetValue(target, out double value) ? value : 0d;

        HMHat GetHat()
        {
            bool up = targetButtons.Contains("dpadUp");
            bool down = targetButtons.Contains("dpadDown");
            bool left = targetButtons.Contains("dpadLeft");
            bool right = targetButtons.Contains("dpadRight");

            return (up, down, left, right) switch
            {
                (true, false, false, false) => HMHat.North,
                (true, false, false, true) => HMHat.NorthEast,
                (false, false, false, true) => HMHat.East,
                (false, true, false, true) => HMHat.SouthEast,
                (false, true, false, false) => HMHat.South,
                (false, true, true, false) => HMHat.SouthWest,
                (false, false, true, false) => HMHat.West,
                (true, false, true, false) => HMHat.NorthWest,
                _ => HMHat.None
            };
        }
    }

    public static GamepadInputSnapshot CreateDisplaySnapshot(
        HMGamepadState state,
        HMProfile profile,
        string? virtualGamepadType = null)
    {
        var axes = new List<GamepadAxisState>();
        var buttons = new List<GamepadButtonState>();
        HMGamepadLayout? layout = null;
        try
        {
            layout = profile.AsGamepad();
        }
        catch
        {
            // Perfiles no/gamepad: se muestra solo lo genérico.
        }

        if (layout is not null)
        {
            foreach (HMStick stick in layout.Sticks)
            {
                string prefix = stick.Side == HMStickSide.Left ? "Stick izq." : "Stick der.";
                AddAxis($"{prefix} X", stick.XAxis, isStick: true);
                AddAxis($"{prefix} Y", stick.YAxis, isStick: true);
            }

            foreach (HMTrigger trigger in layout.Triggers)
            {
                string name = trigger.Side == HMTriggerSide.Left ? "Gatillo izq." : "Gatillo der.";
                AddAxis(name, trigger.Axis, isStick: false);
            }
        }

        bool isDualShock = string.Equals(
            virtualGamepadType, VirtualGamepadTypes.DualShock4, StringComparison.Ordinal);
        AddButton(HMButton.A, "A / Cruz");
        AddButton(HMButton.B, "B / Círculo");
        AddButton(HMButton.X, "X / Cuadrado");
        AddButton(HMButton.Y, "Y / Triángulo");
        AddButton(HMButton.Back, "Atrás / Share");
        AddButton(HMButton.Start, "Inicio / Options");
        AddButton(HMButton.Guide, "Guía / PS");
        AddButton(HMButton.LeftStick, "Stick izq. (L3)");
        AddButton(HMButton.RightStick, "Stick der. (R3)");
        AddButton(HMButton.LeftBumper, "Hombro izq.");
        AddButton(HMButton.RightBumper, "Hombro der.");
        if (isDualShock)
        {
            AddButton(HMButton.Touchpad, "Panel táctil (clic)");
            AddButton(HMButton.Misc1, "Botón extra (Misc 1)");
        }

        bool dpadUp = false, dpadDown = false, dpadLeft = false, dpadRight = false;
        switch (state.Hat)
        {
            case HMHat.North: dpadUp = true; break;
            case HMHat.NorthEast: dpadUp = dpadRight = true; break;
            case HMHat.East: dpadRight = true; break;
            case HMHat.SouthEast: dpadDown = dpadRight = true; break;
            case HMHat.South: dpadDown = true; break;
            case HMHat.SouthWest: dpadDown = dpadLeft = true; break;
            case HMHat.West: dpadLeft = true; break;
            case HMHat.NorthWest: dpadUp = dpadLeft = true; break;
        }

        AddDpadButton("Cruceta arriba", dpadUp);
        AddDpadButton("Cruceta abajo", dpadDown);
        AddDpadButton("Cruceta izquierda", dpadLeft);
        AddDpadButton("Cruceta derecha", dpadRight);

        MotionSensorData? motion = null;
        if (isDualShock)
        {
            const double standardGravity = 9.80665;
            const double degToRad = 0.017453292519943295;
            motion = new MotionSensorData
            {
                HasAccelerometer = true,
                HasGyroscope = true,
                AccelerometerEnabled = true,
                GyroscopeEnabled = true,
                AccelXMs2 = state.AccelGX * standardGravity,
                AccelYMs2 = state.AccelGY * standardGravity,
                AccelZMs2 = state.AccelGZ * standardGravity,
                GyroPitchRadS = state.GyroDpsX * degToRad,
                GyroYawRadS = state.GyroDpsY * degToRad,
                GyroRollRadS = state.GyroDpsZ * degToRad
            };
        }

        TouchpadState? touchpad = null;
        if (isDualShock)
        {
            touchpad = new TouchpadState
            {
                HasTouchpad = true,
                Finger0 = CreateFinger(state.TouchpadFinger0Active, state.TouchpadFinger0X,
                    state.TouchpadFinger0Y, state.TouchpadFinger0Id, state.TouchpadFinger0Pressure),
                Finger1 = CreateFinger(state.TouchpadFinger1Active, state.TouchpadFinger1X,
                    state.TouchpadFinger1Y, state.TouchpadFinger1Id, state.TouchpadFinger1Pressure)
            };
        }

        return new GamepadInputSnapshot
        {
            IsStandardGameController = true,
            Axes = axes,
            Buttons = buttons,
            PhysicalAxisCount = axes.Count,
            PhysicalButtonCount = buttons.Count,
            Motion = motion,
            Touchpad = touchpad,
            BatteryCharging = state.BatteryCharging,
            BatteryPercent = state.BatteryLevel > 0 ? state.BatteryLevel * 10 : null,
            BatteryLevel = state.BatteryCharging
                ? GamepadBatteryLevel.Charging
                : state.BatteryFull
                    ? GamepadBatteryLevel.Full
                    : GamepadBatteryLevel.Unknown
        };

        void AddAxis(string name, HMAxis axis, bool isStick)
        {
            double value = 0d;
            if (state.Axes is not null && state.Axes.TryGetValue(axis, out float raw))
            {
                value = isStick
                    ? Math.Clamp(raw * 2d - 1d, -1d, 1d)
                    : Math.Clamp(raw, 0d, 1d);
            }

            axes.Add(new GamepadAxisState(name, value));
        }

        void AddButton(HMButton flag, string name)
        {
            buttons.Add(new GamepadButtonState(name, state.Buttons.HasFlag(flag)));
        }

        void AddDpadButton(string name, bool pressed)
        {
            buttons.Add(new GamepadButtonState(name, pressed));
        }

        static TouchpadFingerState? CreateFinger(
            bool active, ushort x, ushort y, byte id, float pressure) =>
            active
                ? new TouchpadFingerState
                {
                    Active = true,
                    X = Math.Clamp(x / 1920d, 0d, 1d),
                    Y = Math.Clamp(y / 920d, 0d, 1d),
                    Id = id,
                    Pressure = pressure
                }
                : null;
    }

    private static void ApplyBattery(ref HMGamepadState state, GamepadInputSnapshot snapshot)
    {
        // El mando virtual reporta la misma batería que el físico:
        // porcentaje exacto si existe, si no el nivel SDL en escala 0-10.
        int? level = snapshot.BatteryPercent is int percent
            ? Math.Clamp((percent + 5) / 10, 0, 10)
            : snapshot.BatteryLevel switch
            {
                GamepadBatteryLevel.Empty => 0,
                GamepadBatteryLevel.Low => 2,
                GamepadBatteryLevel.Medium => 5,
                GamepadBatteryLevel.Full => 10,
                GamepadBatteryLevel.Charging => 5,
                GamepadBatteryLevel.Wired => 10,
                _ => (int?)null
            };

        if (level is not null)
        {
            state.BatteryLevel = (byte)level.Value;
        }

        state.BatteryCharging =
            snapshot.BatteryCharging ||
            snapshot.BatteryLevel == GamepadBatteryLevel.Charging;
        state.BatteryFull =
            snapshot.BatteryPercent >= 100 ||
            (!snapshot.BatteryCharging && snapshot.BatteryLevel == GamepadBatteryLevel.Full);
    }
    private static void ApplyMotion(ref HMGamepadState state, MotionSensorData? motion)
    {
        if (motion is null)
        {
            return;
        }

        const double standardGravity = 9.80665;
        const double radToDeg = 57.29577951308232;

        if (motion.HasAccelerometer)
        {
            double accelX = motion.AccelXMs2 / standardGravity;
            double accelY = motion.AccelYMs2 / standardGravity;
            double accelZ = motion.AccelZMs2 / standardGravity;
            state.AccelX = ToSensorShort(accelX, 8192d);
            state.AccelY = ToSensorShort(accelY, 8192d);
            state.AccelZ = ToSensorShort(accelZ, 8192d);
            state.AccelGX = (float)accelX;
            state.AccelGY = (float)accelY;
            state.AccelGZ = (float)accelZ;
        }

        if (motion.HasGyroscope)
        {
            double gyroPitch = motion.GyroPitchRadS * radToDeg;
            double gyroYaw = motion.GyroYawRadS * radToDeg;
            double gyroRoll = motion.GyroRollRadS * radToDeg;
            state.GyroPitch = ToSensorShort(gyroPitch, 16d);
            state.GyroYaw = ToSensorShort(gyroYaw, 16d);
            state.GyroRoll = ToSensorShort(gyroRoll, 16d);
            state.GyroDpsX = (float)gyroPitch;
            state.GyroDpsY = (float)gyroYaw;
            state.GyroDpsZ = (float)gyroRoll;
        }
    }

    private static int _touchPacketCounter;

    private static void ApplyTouchpad(ref HMGamepadState state, TouchpadState? touchpad)
    {
        if (touchpad is not { HasTouchpad: true })
        {
            return;
        }

        state.TouchpadPacketCounter = unchecked((byte)++_touchPacketCounter);
        ApplyTouchpadFinger(ref state, fingerIndex: 0, touchpad.Finger0);
        ApplyTouchpadFinger(ref state, fingerIndex: 1, touchpad.Finger1);
    }

    private static void ApplyTouchpadFinger(
        ref HMGamepadState state, int fingerIndex, TouchpadFingerState? finger)
    {
        if (finger is not { Active: true })
        {
            return;
        }

        // El DS4 reporta el táctil en unidades de hardware (~1920x920),
        // no en el campo de 12 bits completo: SDL divide por esas escalas.
        ushort x = (ushort)Math.Clamp(Math.Round(finger.X * 1920d), 0d, 4095d);
        ushort y = (ushort)Math.Clamp(Math.Round(finger.Y * 920d), 0d, 4095d);
        byte id = unchecked((byte)Math.Clamp(finger.Id, 0, 255));

        if (fingerIndex == 0)
        {
            state.TouchpadFinger0Active = true;
            state.TouchpadFinger0X = x;
            state.TouchpadFinger0Y = y;
            state.TouchpadFinger0Id = id;
            state.TouchpadFinger0Pressure = finger.Pressure;
        }
        else
        {
            state.TouchpadFinger1Active = true;
            state.TouchpadFinger1X = x;
            state.TouchpadFinger1Y = y;
            state.TouchpadFinger1Id = id;
            state.TouchpadFinger1Pressure = finger.Pressure;
        }
    }

    private static short ToSensorShort(double value, double scale) =>
        (short)Math.Clamp(Math.Round(value * scale), short.MinValue, short.MaxValue);

    private static double GetAxis(GamepadInputSnapshot snapshot, string name) =>
        snapshot.Axes.FirstOrDefault(axis => axis.Name == name)?.Value ?? 0d;

    private static float ToUnsignedStickValue(double value) =>
        (float)((Math.Clamp(value, -1d, 1d) + 1d) / 2d);

    private static float ToTriggerValue(double value) =>
        (float)Math.Clamp(value, 0d, 1d);

    private static (double X, double Y) ApplyDeadZone(double x, double y, double deadZone)
    {
        double magnitude = Math.Sqrt(x * x + y * y);
        if (deadZone >= 1d || magnitude <= deadZone)
        {
            return (0d, 0d);
        }

        double scaledMagnitude = (magnitude - deadZone) / (1d - deadZone);
        double scale = scaledMagnitude / magnitude;
        return (x * scale, y * scale);
    }

    private static string NormalizeButtonTarget(string target, string sourceId) =>
        target switch
        {
            "none" => GamepadControlCatalog.NoButtonTarget.Id,
            "A" => "south",
            "B" => "east",
            "X" => "west",
            "Y" => "north",
            _ when GamepadControlCatalog.Buttons.Any(button => button.Id == target) => target,
            _ => sourceId
        };

    private static string NormalizeAxisTarget(string target, string sourceId)
    {
        string normalized = target switch
        {
            "X" or "LX" => "leftX",
            "Y" or "LY" => "leftY",
            "RX" => "rightX",
            "RY" => "rightY",
            "LT" => "leftTrigger",
            "RT" => "rightTrigger",
            _ => target
        };
        return normalized == GamepadControlCatalog.NoButtonTarget.Id ||
               GamepadControlCatalog.Axes.Any(axis => axis.Id == normalized)
            ? normalized
            : sourceId;
    }

    private static bool IsPressed(GamepadInputSnapshot snapshot, string name) =>
        snapshot.Buttons.Any(button => button.Name == name && button.IsPressed);
}
