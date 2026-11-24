using System.IO;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class LocalStoragePathService : IStoragePathService
{
    public string RootDirectory { get; }
    public string SettingsFilePath => Path.Combine(RootDirectory, "Settings", "app-settings.json");
    public string ControllersIndexFilePath => Path.Combine(RootDirectory, "Controllers", "controllers-index.json");
    public string ProfilesIndexFilePath => Path.Combine(RootDirectory, "Profiles", "profiles-index.json");
    public string GlobalPresetFilePath => Path.Combine(RootDirectory, "Settings", "global-preset.json");
    public string LogFilePath { get; }

    public LocalStoragePathService(string? rootDirectory = null)
    {
        string path = rootDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GDM");

        RootDirectory = Path.GetFullPath(path);
        LogFilePath = ResolveLogFilePath(RootDirectory);
    }

    private static string ResolveLogFilePath(string rootDirectory)
    {
        string preferred = Path.Combine(AppContext.BaseDirectory, "Logs", "gdm.log");
        try
        {
            string? directory = Path.GetDirectoryName(preferred);
            if (directory is null)
            {
                return Path.Combine(rootDirectory, "Logs", "gdm.log");
            }

            Directory.CreateDirectory(directory);
            string probePath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return preferred;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return Path.Combine(rootDirectory, "Logs", "gdm.log");
        }
    }

    public string GetControllerFilePath(Guid controllerId)
    {
        if (controllerId == Guid.Empty)
        {
            throw new ArgumentException("El identificador del mando no puede estar vacío.", nameof(controllerId));
        }

        return Path.Combine(
            RootDirectory,
            "Controllers",
            $"controller_{controllerId:N}.json");
    }

    public string GetProfileFilePath(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("El identificador del perfil no puede estar vacío.", nameof(profileId));
        }

        return Path.Combine(
            RootDirectory,
            "Profiles",
            $"profile_{profileId:N}.json");
    }
}
