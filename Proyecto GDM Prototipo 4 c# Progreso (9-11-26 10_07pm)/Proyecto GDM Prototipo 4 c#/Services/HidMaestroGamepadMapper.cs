using GDM.Models;
using HIDMaestro;

namespace GDM.Services;

public static class HidMaestroGamepadMapper
{
    public static HMGamepadState CreateState(
        GamepadInputSnapshot snapshot,
        HMProfile profile,
        ControllerProfile? mappingProfile = null)
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
            if (!string.Equals(target, GamepadControlCatalog.NoButtonTarget.Id, StringComparison.Ordinal))
            {
                targetButtons.Add(target);
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

        return new HMGamepadState
        {
            Axes = axes,
            Buttons = buttons,
            Hat = GetHat()
        };

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
        return GamepadControlCatalog.Axes.Any(axis => axis.Id == normalized)
            ? normalized
            : sourceId;
    }

    private static bool IsPressed(GamepadInputSnapshot snapshot, string name) =>
        snapshot.Buttons.Any(button => button.Name == name && button.IsPressed);
}
