using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IVirtualGamepadService : IDisposable
{
    bool IsRunning { get; }
    IReadOnlyCollection<string> RunningDeviceKeys { get; }
    bool IsAdministrator { get; }
    string? SourceDeviceName { get; }
    bool IsRunningFor(string deviceKey);
    bool RebindDevice(string previousDeviceKey, ConnectedGamepad device);
    Task StartAsync(ConnectedGamepad device, CancellationToken cancellationToken = default);
    Task StartAsync(
        ConnectedGamepad device,
        GamepadInputSnapshot initialSnapshot,
        ControllerProfile? mappingProfile,
        CancellationToken cancellationToken = default);
    void SubmitInputSnapshot(ConnectedGamepad device, GamepadInputSnapshot snapshot);
    void SubmitInputSnapshot(
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? mappingProfile);
    Task StopAsync(string deviceKey, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
