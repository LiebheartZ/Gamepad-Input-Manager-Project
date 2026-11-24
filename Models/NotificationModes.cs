namespace GDM.Models;

public static class NotificationModes
{
    public const string Always = "always";
    public const string WindowOnly = "window";
    public const string Off = "off";

    public static bool IsSupported(string? value) =>
        value is Always or WindowOnly or Off;

    public static string Normalize(string? value) =>
        IsSupported(value) ? value! : Always;
}
