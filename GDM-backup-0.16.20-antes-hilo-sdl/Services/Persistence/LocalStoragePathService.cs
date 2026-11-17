using System.IO;
using GDM.Services.Abstractions;

namespace GDM.Services.Persistence;

public sealed class LocalStoragePathService : IStoragePathService
{
    public string RootDirectory { get; }
    public string SettingsFilePath => Path.Combine(RootDirectory, "Settings", "app-settings.json");
    public string ControllersIndexFilePath => Path.Combine(RootDirectory, "Controllers", "controllers-index.json");
    public string ProfilesIndexFilePath => Path.Combine(RootDirectory, "Profiles", "profiles-index.json");
    public string LogFilePath => Path.Combine(RootDirectory, "Logs", "gdm.log");

    public LocalStoragePathService(string? rootDirectory = null)
    {
        string path = rootDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GDM");

        RootDirectory = Path.GetFullPath(path);
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
