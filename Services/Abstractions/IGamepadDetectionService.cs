using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IGamepadDetectionService : IDisposable
{
    IReadOnlyList<ConnectedGamepad> RedundantInterfaces { get; }
    event Action? DeviceMembershipChanged;
    void PumpDeviceEvents();
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConnectedGamepad>> EnumerateConnectedAsync(
        CancellationToken cancellationToken = default);
}
