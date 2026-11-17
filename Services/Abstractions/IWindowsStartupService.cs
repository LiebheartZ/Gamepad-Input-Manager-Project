namespace GDM.Services.Abstractions;

public interface IWindowsStartupService
{
    Task ConfigureAsync(
        bool startWithWindows,
        bool startMinimized,
        bool startWithWindowsAsTask);
}
