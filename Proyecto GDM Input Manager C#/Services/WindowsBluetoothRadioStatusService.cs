using Windows.Devices.Radios;

namespace GDMInputManager.Services;

public enum BluetoothRadioAvailability
{
    On,
    Off,
    Unavailable,
    Unknown
}

public sealed record BluetoothRadioStatus(
    BluetoothRadioAvailability Availability,
    string Detail);

public sealed class WindowsBluetoothRadioStatusService
{
    public async Task<BluetoothRadioStatus> GetStatusAsync()
    {
        try
        {
            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            Radio[] bluetoothRadios = radios
                .Where(radio => radio.Kind == RadioKind.Bluetooth)
                .ToArray();

            if (bluetoothRadios.Length == 0)
            {
                return new BluetoothRadioStatus(
                    BluetoothRadioAvailability.Unavailable,
                    "Windows no encontró un adaptador Bluetooth.");
            }

            if (bluetoothRadios.Any(radio => radio.State == RadioState.On))
            {
                return new BluetoothRadioStatus(
                    BluetoothRadioAvailability.On,
                    "Bluetooth está encendido.");
            }

            if (bluetoothRadios.All(radio => radio.State is RadioState.Off or RadioState.Disabled))
            {
                return new BluetoothRadioStatus(
                    BluetoothRadioAvailability.Off,
                    "Bluetooth está apagado o deshabilitado.");
            }

            return new BluetoothRadioStatus(
                BluetoothRadioAvailability.Unknown,
                "Windows no pudo determinar si Bluetooth está encendido.");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning(
                $"No se pudo consultar el estado del adaptador Bluetooth: {ex.Message}");
            return new BluetoothRadioStatus(
                BluetoothRadioAvailability.Unknown,
                $"Estado Bluetooth desconocido: {ex.Message}");
        }
    }
}
