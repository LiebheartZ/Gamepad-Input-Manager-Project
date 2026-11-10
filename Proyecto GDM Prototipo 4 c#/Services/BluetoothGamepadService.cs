using System.Collections.Concurrent;
using GDM.Models;
using GDM.Services.Abstractions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace GDM.Services;

public sealed class BluetoothGamepadService : IBluetoothGamepadService, IDisposable
{
    private const string BluetoothLEProtocolId = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    private const string AqsFilter = $"(System.Devices.Aep.ProtocolId:=\"{BluetoothLEProtocolId}\")";

    private readonly ConcurrentDictionary<string, BluetoothGamepad> _devices = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<BluetoothGamepad>? DeviceAdded;
    public event EventHandler<BluetoothGamepad>? DeviceRemoved;
    public event EventHandler<BluetoothGamepad>? DeviceUpdated;
    public event EventHandler<BluetoothGamepad>? DeviceConnected;

    public Task StartWatcherAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher is not null)
        {
            return Task.CompletedTask;
        }

        _watcher = DeviceInformation.CreateWatcher(
            AqsFilter,
            ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired"]);

        _watcher.Added += OnDeviceAdded;
        _watcher.Removed += OnDeviceRemoved;
        _watcher.Updated += OnDeviceUpdated;
        _watcher.Start();

        return Task.CompletedTask;
    }

    public async Task StopWatcherAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher is null)
        {
            return;
        }

        _watcher.Stop();
        _watcher.Added -= OnDeviceAdded;
        _watcher.Removed -= OnDeviceRemoved;
        _watcher.Updated -= OnDeviceUpdated;
        _watcher = null;

        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BluetoothGamepad>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string[] requestedProperties =
        [
            "System.Devices.Aep.DeviceAddress",
            "System.Devices.Aep.IsConnected",
            "System.Devices.Aep.IsPaired"
        ];

        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(AqsFilter, requestedProperties);

        List<BluetoothGamepad> gamepads = [];
        foreach (DeviceInformation device in devices)
        {
            BluetoothGamepad? gamepad = CreateGamepadFromDevice(device);
            if (gamepad is not null)
            {
                gamepads.Add(gamepad);
                _devices.TryAdd(gamepad.Id, gamepad);
            }
        }

        return gamepads;
    }

    public async Task<bool> PairAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_devices.TryGetValue(deviceId, out BluetoothGamepad? gamepad))
        {
            return false;
        }

        gamepad.SetPairing(true);

        try
        {
            DeviceInformation deviceInfo = await DeviceInformation.CreateFromIdAsync(deviceId);
            if (deviceInfo is null)
            {
                return false;
            }

            DevicePairingResult result = await deviceInfo.Pairing.PairAsync();

            if (result.Status == DevicePairingStatus.Paired || result.Status == DevicePairingStatus.AlreadyPaired)
            {
                gamepad.UpdateStatus(true, deviceInfo.Pairing.IsPaired);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            gamepad.SetPairing(false);
        }
    }

    public async Task<bool> UnpairAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_devices.TryGetValue(deviceId, out BluetoothGamepad? gamepad))
        {
            return false;
        }

        gamepad.SetUnpairing(true);

        try
        {
            DeviceInformation deviceInfo = await DeviceInformation.CreateFromIdAsync(deviceId);
            if (deviceInfo is null)
            {
                return false;
            }

            DeviceUnpairingResult result = await deviceInfo.Pairing.UnpairAsync();

            if (result.Status == DeviceUnpairingStatus.Unpaired)
            {
                gamepad.UpdateStatus(false, false);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            gamepad.SetUnpairing(false);
        }
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device)
    {
        BluetoothGamepad? gamepad = CreateGamepadFromDevice(device);
        if (gamepad is not null && _devices.TryAdd(gamepad.Id, gamepad))
        {
            DeviceAdded?.Invoke(this, gamepad);
        }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_devices.TryRemove(update.Id, out BluetoothGamepad? gamepad))
        {
            DeviceRemoved?.Invoke(this, gamepad);
        }
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_devices.TryGetValue(update.Id, out BluetoothGamepad? gamepad))
        {
            bool wasConnected = gamepad.IsConnected;
            bool isNowConnected = update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? connectedObj) &&
                                 connectedObj is bool connected &&
                                 connected;

            bool isPaired = update.Properties.TryGetValue("System.Devices.Aep.IsPaired", out object? pairedObj) &&
                           pairedObj is bool paired &&
                           paired;

            gamepad.UpdateStatus(isPaired, isNowConnected);

            DeviceUpdated?.Invoke(this, gamepad);

            if (!wasConnected && isNowConnected)
            {
                DeviceConnected?.Invoke(this, gamepad);
            }
        }
    }

    private static BluetoothGamepad? CreateGamepadFromDevice(DeviceInformation device)
    {
        string name = device.Name ?? "Dispositivo desconocido";
        bool isRecognized = BluetoothGamepadCatalog.IsRecognizedGamepad(name);

        if (!ulong.TryParse(
                device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object? addressObj)
                    ? addressObj.ToString()
                    : "0",
                out ulong bluetoothAddress))
        {
            bluetoothAddress = 0;
        }

        bool isPaired = device.Pairing.IsPaired;
        bool isConnected = device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? connectedObj) &&
                           connectedObj is bool connected &&
                           connected;

        return new BluetoothGamepad
        {
            Id = device.Id,
            Name = name,
            BluetoothAddress = bluetoothAddress,
            IsRecognizedGamepad = isRecognized
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_watcher is not null)
        {
            _watcher.Stop();
            _watcher.Added -= OnDeviceAdded;
            _watcher.Removed -= OnDeviceRemoved;
            _watcher.Updated -= OnDeviceUpdated;
            _watcher = null;
        }

        _devices.Clear();
        _disposed = true;
    }
}
