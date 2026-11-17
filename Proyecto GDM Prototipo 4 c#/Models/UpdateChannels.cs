namespace GDM.Models;

public static class UpdateChannels
{
    public const string Standard = "standard";
    public const string Nightly = "nightly";

    public static bool IsSupported(string? value) =>
        value is Standard or Nightly;

    public static string Normalize(string? value) =>
        value is Nightly ? Nightly : Standard;
}
