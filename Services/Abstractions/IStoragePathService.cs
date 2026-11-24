namespace GDM.Services.Abstractions;

public interface IStoragePathService
{
    string RootDirectory { get; }
    string SettingsFilePath { get; }
    string ControllersIndexFilePath { get; }
    string ProfilesIndexFilePath { get; }
    string GlobalPresetFilePath { get; }
    string LogFilePath { get; }
    string GetControllerFilePath(Guid controllerId);
    string GetProfileFilePath(Guid profileId);
}
