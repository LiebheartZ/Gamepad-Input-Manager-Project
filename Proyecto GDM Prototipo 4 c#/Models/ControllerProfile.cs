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
    public Dictionary<string, ControllerProfileMapping> TypeMappings { get; init; } = [];
    public string VirtualGamepadType { get; init; } = VirtualGamepadTypes.Xbox360;
    public List<ControllerCombo> Combos { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }

    public ControllerProfileMapping GetMappingsFor(string type)
    {
        string normalized = VirtualGamepadTypes.Normalize(type);
        if (TypeMappings.TryGetValue(normalized, out ControllerProfileMapping? mappings) &&
            mappings is not null)
        {
            return mappings;
        }

        if (TypeMappings.Count == 0)
        {
            return new ControllerProfileMapping
            {
                ButtonMappings = ButtonMappings,
                AxisMappings = AxisMappings,
                AxisInversions = AxisInversions,
                DeadZones = DeadZones
            };
        }

        return new ControllerProfileMapping();
    }

    public ControllerProfile WithMappingsFor(string type)
    {
        ControllerProfileMapping mappings = GetMappingsFor(type);
        return this with
        {
            ButtonMappings = mappings.ButtonMappings,
            AxisMappings = mappings.AxisMappings,
            AxisInversions = mappings.AxisInversions,
            DeadZones = mappings.DeadZones
        };
    }
}
