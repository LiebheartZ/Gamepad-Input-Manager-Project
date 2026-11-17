namespace GDM.Models;

public sealed record GamepadControlOption(string Id, string DisplayName, string? InputName = null);

public static class GamepadControlCatalog
{
    public static GamepadControlOption NoButtonTarget { get; } = new("none", "Ninguno");

    public static IReadOnlyList<GamepadControlOption> Buttons { get; } =
    [
        new("south", "A / Cruz", "A / Cruz"),
        new("east", "B / Círculo", "B / Círculo"),
        new("west", "X / Cuadrado", "X / Cuadrado"),
        new("north", "Y / Triángulo", "Y / Triángulo"),
        new("leftBumper", "Hombro izquierdo", "Hombro izq."),
        new("rightBumper", "Hombro derecho", "Hombro der."),
        new("back", "Atrás / Share", "Atrás / Share"),
        new("start", "Inicio / Options", "Inicio / Options"),
        new("guide", "Guía / PS", "Guía / PS"),
        new("leftStick", "L3", "Stick izq. (L3)"),
        new("rightStick", "R3", "Stick der. (R3)"),
        new("touchpad", "Panel táctil", "Panel táctil (clic)"),
        new("misc1", "Botón extra", "Botón extra (Misc 1)"),
        new("paddle1", "Pala 1", "Pala 1"),
        new("paddle2", "Pala 2", "Pala 2"),
        new("paddle3", "Pala 3", "Pala 3"),
        new("paddle4", "Pala 4", "Pala 4"),
        new("dpadUp", "Cruceta arriba", "Cruceta arriba"),
        new("dpadDown", "Cruceta abajo", "Cruceta abajo"),
        new("dpadLeft", "Cruceta izquierda", "Cruceta izquierda"),
        new("dpadRight", "Cruceta derecha", "Cruceta derecha")
    ];

    public static IReadOnlyList<GamepadControlOption> ButtonTargets { get; } =
        [NoButtonTarget, .. Buttons];

    private static readonly HashSet<string> Xbox360UnsupportedTargets = new(StringComparer.Ordinal)
    {
        "touchpad", "misc1", "paddle1", "paddle2", "paddle3", "paddle4"
    };

    private static readonly HashSet<string> DualShock4UnsupportedTargets = new(StringComparer.Ordinal)
    {
        "misc1", "paddle1", "paddle2", "paddle3", "paddle4"
    };

    public static IReadOnlyList<GamepadControlOption> GetButtonTargetsFor(string? virtualGamepadType) =>
        [.. ButtonTargets.Where(option => IsButtonTargetSupported(option.Id, virtualGamepadType))];

    public static bool IsButtonTargetSupported(string targetId, string? virtualGamepadType)
    {
        if (targetId == NoButtonTarget.Id || virtualGamepadType is null)
        {
            return true;
        }

        HashSet<string>? unsupported = virtualGamepadType switch
        {
            VirtualGamepadTypes.Xbox360 => Xbox360UnsupportedTargets,
            VirtualGamepadTypes.DualShock4 => DualShock4UnsupportedTargets,
            _ => null
        };
        return unsupported?.Contains(targetId) != true;
    }

    public static IReadOnlyList<GamepadControlOption> Axes { get; } =
    [
        new("leftX", "Stick izquierdo X", "Stick izq. X"),
        new("leftY", "Stick izquierdo Y", "Stick izq. Y"),
        new("rightX", "Stick derecho X", "Stick der. X"),
        new("rightY", "Stick derecho Y", "Stick der. Y"),
        new("leftTrigger", "Gatillo izquierdo", "Gatillo izq."),
        new("rightTrigger", "Gatillo derecho", "Gatillo der.")
    ];
}
