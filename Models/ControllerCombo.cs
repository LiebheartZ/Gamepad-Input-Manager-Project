namespace GDM.Models;

public sealed record ComboActionOption(string Id, string DisplayName);

public static class ComboActionTypes
{
    public const string Buttons = "buttons";
    public const string StopOutput = "stopOutput";
    public const string SwitchType = "switchType";
    public const string PowerOff = "powerOff";

    public static IReadOnlyList<ComboActionOption> Options { get; } =
    [
        new(Buttons, "Pulsar botones en el mando virtual"),
        new(StopOutput, "Alternar salida virtual (encender/apagar)"),
        new(SwitchType, "Cambiar tipo de mando virtual"),
        new(PowerOff, "Apagar el mando físico")
    ];

    public static bool IsSupported(string? actionType) =>
        string.Equals(actionType, Buttons, StringComparison.Ordinal) ||
        string.Equals(actionType, StopOutput, StringComparison.Ordinal) ||
        string.Equals(actionType, SwitchType, StringComparison.Ordinal) ||
        string.Equals(actionType, PowerOff, StringComparison.Ordinal);

    public static string Normalize(string? actionType) =>
        IsSupported(actionType) ? actionType! : Buttons;
}

public sealed record ControllerCombo
{
    public Guid ComboId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public List<string> Buttons { get; init; } = [];
    public bool RequiresHold { get; init; }
    public int HoldMilliseconds { get; init; }
    public string ActionType { get; init; } = ComboActionTypes.Buttons;
    public List<string> TargetButtons { get; init; } = [];
}
