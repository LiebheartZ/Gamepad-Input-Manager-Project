using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IVirtualGamepadService : IDisposable
{
    bool IsRunning { get; }
    IReadOnlyCollection<string> RunningDeviceKeys { get; }
    bool IsAdministrator { get; }
    string? SourceDeviceName { get; }
    bool IsRunningFor(string deviceKey);
    event Action<string, int, int>? RumbleReceived;
    string? GetRunningVirtualGamepadType(string deviceKey);
    GamepadInputSnapshot? TryGetVirtualOutputSnapshot(ConnectedGamepad virtualDevice);
    bool RebindDevice(string previousDeviceKey, ConnectedGamepad device);
    Task StartAsync(
        ConnectedGamepad device,
        string virtualGamepadType,
        CancellationToken cancellationToken = default);
    Task StartAsync(
        ConnectedGamepad device,
        GamepadInputSnapshot initialSnapshot,
        ControllerProfile? mappingProfile,
        string virtualGamepadType,
        CancellationToken cancellationToken = default);
    void SubmitInputSnapshot(ConnectedGamepad device, GamepadInputSnapshot snapshot);
    void SubmitInputSnapshot(
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? mappingProfile,
        IReadOnlyCollection<string>? additionalTargetButtons = null,
        int? motionIntervalMs = null);
    Task StopAsync(string deviceKey, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
