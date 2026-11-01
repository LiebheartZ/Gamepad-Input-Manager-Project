namespace GDMInputManager.Interfaces;

public interface IBluetoothDeviceActions
{
    Task UnpairAsync(string bluetoothAddress, CancellationToken cancellationToken);
    Task ShowPickerAsync(IntPtr ownerHandle, Rectangle anchor, CancellationToken cancellationToken);
}
