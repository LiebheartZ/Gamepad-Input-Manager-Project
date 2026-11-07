using GDMInputManager.Models;

namespace GDMInputManager.Interfaces;

public sealed class GamepadListChangedEventArgs(IReadOnlyList<GamepadListItem> items) : EventArgs
{
    public IReadOnlyList<GamepadListItem> Items { get; } = items;
}

public sealed class GamepadErrorEventArgs(string source, string message, Exception? exception = null) : EventArgs
{
    public string Source { get; } = source;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public sealed class GamepadRefreshCompletedEventArgs(
    bool succeeded,
    IReadOnlyList<string> warnings) : EventArgs
{
    public bool Succeeded { get; } = succeeded;
    public IReadOnlyList<string> Warnings { get; } = warnings;
}

public interface IGamepadManager : IDisposable
{
    IReadOnlyList<GamepadListItem> CurrentItems { get; }
    event EventHandler<GamepadListChangedEventArgs>? ListChanged;
    event EventHandler<GamepadErrorEventArgs>? ErrorOccurred;
    event EventHandler<GamepadRefreshCompletedEventArgs>? RefreshCompleted;

    void SetMonitoringActive(bool isActive);
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(bool includeAvailableBluetooth = false, CancellationToken cancellationToken = default);
    Task<GamepadRecord> SaveDetectedDeviceAsync(string deviceKey, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid recordId, string displayName, CancellationToken cancellationToken = default);
    Task RemoveRecordAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task UnpairBluetoothAsync(string bluetoothAddress, CancellationToken cancellationToken = default);
    Task ShowBluetoothPickerAsync(
        IntPtr ownerHandle,
        Rectangle anchor,
        CancellationToken cancellationToken = default);
}
