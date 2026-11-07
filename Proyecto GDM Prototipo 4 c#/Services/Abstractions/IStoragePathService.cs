namespace GDM.Services.Abstractions;

public interface IStoragePathService
{
    string RootDirectory { get; }
    string SettingsFilePath { get; }
    string ControllersIndexFilePath { get; }
    string LogFilePath { get; }
    string GetControllerFilePath(Guid controllerId);
}
