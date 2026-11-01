using System.ComponentModel;
using System.Globalization;
using GDMInputManager.Interfaces;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace GDMInputManager.Infrastructure.Windows;

public sealed class WindowsBluetoothDeviceActions : IBluetoothDeviceActions
{
    public async Task UnpairAsync(string bluetoothAddress, CancellationToken cancellationToken)
    {
        string normalizedAddress = string.Concat((bluetoothAddress ?? string.Empty).Where(Uri.IsHexDigit));
        if (normalizedAddress.Length != 12 ||
            !ulong.TryParse(normalizedAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
        {
            throw new ArgumentException("La dirección Bluetooth no es válida.", nameof(bluetoothAddress));
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int result = BluetoothRemoveDevice(ref address);
            if (result != 0)
            {
                throw new Win32Exception(result, "Windows no pudo quitar el emparejamiento Bluetooth.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ShowPickerAsync(
        IntPtr ownerHandle,
        Rectangle anchor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new DevicePicker();
        picker.Filter.SupportedDeviceSelectors.Add(
            BluetoothDevice.GetDeviceSelectorFromPairingState(false));
        picker.Filter.SupportedDeviceSelectors.Add(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(false));
        picker.Appearance.Title = "Agregar un dispositivo";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHandle);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int selectionStarted = 0;
        picker.DeviceSelected += async (_, args) =>
        {
            if (Interlocked.Exchange(ref selectionStarted, 1) != 0)
            {
                return;
            }

            try
            {
                DevicePairingResult result = await args.SelectedDevice.Pairing.PairAsync();
                if (result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                {
                    completion.TrySetResult();
                }
                else
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"Windows no pudo emparejar el dispositivo (estado: {result.Status})."));
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        };
        picker.DevicePickerDismissed += (_, _) =>
        {
            if (Volatile.Read(ref selectionStarted) == 0)
            {
                completion.TrySetResult();
            }
        };

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        picker.Show(new global::Windows.Foundation.Rect(anchor.X, anchor.Y, anchor.Width, anchor.Height));
        await completion.Task.ConfigureAwait(true);
    }

    [System.Runtime.InteropServices.DllImport("bthprops.cpl", SetLastError = true)]
    private static extern int BluetoothRemoveDevice(ref ulong address);
}
