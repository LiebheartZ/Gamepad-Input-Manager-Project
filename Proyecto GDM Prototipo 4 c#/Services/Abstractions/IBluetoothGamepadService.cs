using GDM.Models;

namespace GDM.Services.Abstractions;

public interface IBluetoothGamepadService
{
    event EventHandler<BluetoothGamepad>? DeviceAdded;
    event EventHandler<BluetoothGamepad>? DeviceRemoved;
    event EventHandler<BluetoothGamepad>? DeviceUpdated;
    event EventHandler<BluetoothGamepad>? DeviceConnected;

    Task StartWatcherAsync(CancellationToken cancellationToken = default);
    Task StopWatcherAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BluetoothGamepad>> EnumerateAsync(CancellationToken cancellationToken = default);
    Task<bool> PairAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> UnpairAsync(string deviceId, CancellationToken cancellationToken = default);
}
