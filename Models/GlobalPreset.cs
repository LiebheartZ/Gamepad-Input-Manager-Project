namespace GDM.Models;

public enum GlobalPresetUnsavedMode
{
    None = 0,
    ViewOnly = 1,
    TempTab = 2
}

public static class GlobalPresetModes
{
    public static bool IsSupported(int value) => value is >= (int)GlobalPresetUnsavedMode.None and <= (int)GlobalPresetUnsavedMode.TempTab;

    public static GlobalPresetUnsavedMode Normalize(int value) =>
        IsSupported(value) ? (GlobalPresetUnsavedMode)value : GlobalPresetUnsavedMode.None;
}

public sealed record GlobalPreset
{
    public const int CurrentSchemaVersion = 1;

    public static GlobalPreset Default { get; } = new()
    {
        Settings = new ControllerSettings
        {
            Led = ControllerLedSettings.Default with
            {
                Mode = ControllerLedModes.VirtualOutput
            }
        }
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ControllerSettings Settings { get; init; } = new();
    public GlobalPresetUnsavedMode UnsavedDevices { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
