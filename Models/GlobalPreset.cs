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
    public NewControllerSaveCombo SaveNewControllerCombo { get; init; } = new();
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// Combo global que guarda un mando físico no registrado como si se pulsara
/// el botón «Guardar». Solo vive en el ajuste global; no es heredable por mando.
/// </summary>
public sealed record NewControllerSaveCombo
{
    public const int DefaultHoldMilliseconds = 3000;
    public const int MinHoldMilliseconds = 500;
    public const int MaxHoldMilliseconds = 10000;

    public bool Enabled { get; init; } = true;
    public List<string> Buttons { get; init; } = ["back", "start"];
    public int HoldMilliseconds { get; init; } = DefaultHoldMilliseconds;

    public static int NormalizeHold(int value) =>
        Math.Clamp(value, MinHoldMilliseconds, MaxHoldMilliseconds);

    public NewControllerSaveCombo Normalize()
    {
        var validButtons = Buttons
            .Where(button => GamepadControlCatalog.Buttons.Any(option => option.Id == button))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToList();
        return this with
        {
            Buttons = validButtons.Count == 0 ? ["back", "start"] : validButtons,
            HoldMilliseconds = NormalizeHold(HoldMilliseconds)
        };
    }
}
