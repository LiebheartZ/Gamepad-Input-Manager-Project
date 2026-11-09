namespace GDM.Models;

public sealed record ControllerRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid ControllerId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ControllerIdentity Identity { get; init; } = new();
    public Guid? ProfileId { get; init; }
    public Dictionary<string, string> ButtonMappingOverrides { get; init; } = [];
    public Dictionary<string, string> AxisMappingOverrides { get; init; } = [];
    public Dictionary<string, double> DeadZoneOverrides { get; init; } = [];
    public ControllerSettings Settings { get; init; } = new();
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ControllerIdentity
{
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? BluetoothMac { get; init; }
    public string? SdlGuid { get; init; }
    public IReadOnlyList<ControllerIdentityEvidence> Evidence { get; init; } = [];
}

public sealed record ControllerSettings
{
    public bool VibrationEnabled { get; init; } = true;
    public bool AutoStartVirtualGamepad { get; init; }
    public bool AutoHidePhysicalGamepad { get; init; }
    public bool KeepHiddenOnExit { get; init; }
}

public sealed record ControllerIdentityEvidence
{
    public string Source { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}
