namespace GDM.Models;

public sealed record ControllerProfileMapping
{
    public Dictionary<string, string> ButtonMappings { get; init; } = [];
    public Dictionary<string, string> AxisMappings { get; init; } = [];
    public Dictionary<string, bool> AxisInversions { get; init; } = [];
    public Dictionary<string, double> DeadZones { get; init; } = [];
}
