namespace GDM.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Theme { get; init; } = "System";
    public string Language { get; init; } = "es";
    public bool CheckForUpdatesOnStartup { get; init; } = true;
    public double WindowWidth { get; init; } = 800;
    public double WindowHeight { get; init; } = 600;
}
