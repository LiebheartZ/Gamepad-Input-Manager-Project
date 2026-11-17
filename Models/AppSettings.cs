namespace GDM.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Theme { get; init; } = "System";
    public string Language { get; init; } = "es";
    public bool CheckForUpdatesOnStartup { get; init; } = true;
    public bool SkipUpdateNotifications { get; init; }
    public string UpdateChannel { get; init; } = UpdateChannels.Standard;
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; }
    public bool StartWithWindowsAsTask { get; init; }
    public string DefaultVirtualGamepadType { get; init; } = VirtualGamepadTypes.Xbox360;
    public string Xbox360ProfileVariant { get; init; } = VirtualGamepadTypes.Xbox360VariantDefault;
    public Guid? DefaultProfileId { get; init; }
    public bool RestoreGamepadsOnExit { get; init; } = true;
    public bool ShowVirtualGamepadsInAllTabs { get; init; }
    public int VirtualInputPollIntervalMs { get; init; } = 8;
    public string CloseButtonBehavior { get; init; } = CloseButtonBehaviors.HideToTray;
    public bool ShowExitConfirmation { get; init; } = true;
    public double WindowWidth { get; init; } = 800;
    public double WindowHeight { get; init; } = 600;
}
