namespace GDM.Models;

public static class CloseButtonBehaviors
{
    public const string HideToTray = "tray";
    public const string Exit = "close";

    public static bool IsSupported(string? value) =>
        value is HideToTray or Exit;

    public static string Normalize(string? value) =>
        value == Exit ? Exit : HideToTray;
}
