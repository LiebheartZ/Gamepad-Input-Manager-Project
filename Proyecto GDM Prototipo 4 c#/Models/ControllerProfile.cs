namespace GDM.Models;

public sealed record ControllerProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid ProfileId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public Dictionary<string, string> ButtonMappings { get; init; } = [];
    public Dictionary<string, string> AxisMappings { get; init; } = [];
    public Dictionary<string, bool> AxisInversions { get; init; } = [];
    public Dictionary<string, double> DeadZones { get; init; } = [];
    public string VirtualGamepadType { get; init; } = VirtualGamepadTypes.Xbox360;
    public List<ControllerCombo> Combos { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
