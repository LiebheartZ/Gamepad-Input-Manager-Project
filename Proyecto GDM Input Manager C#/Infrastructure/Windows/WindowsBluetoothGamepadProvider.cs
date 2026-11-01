using GDMInputManager.Interfaces;
using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Infrastructure.Windows;

public sealed class WindowsBluetoothGamepadProvider :
    IGamepadProvider,
    IOptionalGamepadProvider
{
    private readonly WindowsBluetoothConnectionService _service = new();

    public string Name => "Windows Bluetooth";

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GamepadDevice>> EnumerateAsync(CancellationToken cancellationToken)
    {
        return EnumerateAsync(includeAvailableDevices: false, cancellationToken);
    }

    public async Task<IReadOnlyList<GamepadDevice>> EnumerateAsync(
        bool includeAvailableDevices,
        CancellationToken cancellationToken)
    {
        return await Task.Run<IReadOnlyList<GamepadDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = new List<GamepadDevice>();
            foreach (WindowsBluetoothDevice bluetoothDevice in _service.GetDevices(includeAvailableDevices))
            {
                cancellationToken.ThrowIfCancellationRequested();
                GamepadType type = Classify(bluetoothDevice.Name);
                if (type == GamepadType.Unknown)
                {
                    continue;
                }

                string address = bluetoothDevice.Address.ToString("X12");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                results.Add(new GamepadDevice
                {
                    DeviceKey = $"{Name}:{address}",
                    Provider = Name,
                    Name = bluetoothDevice.Name,
                    Identity = new GamepadIdentity { BluetoothAddress = address },
                    GamepadType = type,
                    ConnectionType = bluetoothDevice.IsConnected ? ConnectionType.Bluetooth : ConnectionType.Unknown,
                    IsConnected = bluetoothDevice.IsConnected,
                    IsPaired = bluetoothDevice.IsRemembered || bluetoothDevice.IsAuthenticated,
                    CanUnpair = bluetoothDevice.IsRemembered || bluetoothDevice.IsAuthenticated,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }

    private static GamepadType Classify(string name)
    {
        string normalizedName = new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalizedName.Contains("DUALSENSE", StringComparison.Ordinal))
        {
            return GamepadType.PlayStationDualSense;
        }

        if (normalizedName is "PS4CONTROLLER" or "WIRELESSCONTROLLER" or "DUALSHOCK4WIRELESSCONTROLLER")
        {
            return GamepadType.PlayStationDualShock4;
        }

        if (normalizedName.Contains("XBOX", StringComparison.Ordinal))
        {
            return normalizedName.Contains("SERIES", StringComparison.Ordinal)
                ? GamepadType.XboxSeries
                : GamepadType.XboxOne;
        }

        return normalizedName.Contains("CONTROLLER", StringComparison.Ordinal) ||
               normalizedName.Contains("GAMEPAD", StringComparison.Ordinal)
            ? GamepadType.GenericBluetooth
            : GamepadType.Unknown;
    }
}
