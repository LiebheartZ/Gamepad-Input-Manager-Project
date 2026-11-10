using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.ViewModels;

public sealed class BluetoothPairingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IBluetoothGamepadService _bluetoothService;
    private readonly IAppLogService? _appLogService;
    private bool _showAllDevices;
    private bool _enableAutoConnect;
    private bool _isRefreshing;
    private bool _disposed;

    public ObservableCollection<BluetoothGamepad> Devices { get; } = [];
    public ObservableCollection<BluetoothGamepad> FilteredDevices { get; } = [];

    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (SetProperty(ref _showAllDevices, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool EnableAutoConnect
    {
        get => _enableAutoConnect;
        set => SetProperty(ref _enableAutoConnect, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncParameterCommand PairCommand { get; }
    public AsyncParameterCommand UnpairCommand { get; }

    public BluetoothPairingViewModel(
        IBluetoothGamepadService bluetoothService,
        IAppLogService? appLogService = null,
        bool showAllDevices = false,
        bool enableAutoConnect = false)
    {
        _bluetoothService = bluetoothService ?? throw new ArgumentNullException(nameof(bluetoothService));
        _appLogService = appLogService;
        _showAllDevices = showAllDevices;
        _enableAutoConnect = enableAutoConnect;

        RefreshCommand = new AsyncCommand(
            () => RefreshAsync(),
            () => !IsRefreshing);
        PairCommand = new AsyncParameterCommand(
            parameter => parameter is BluetoothGamepad device ? PairAsync(device) : Task.CompletedTask,
            parameter => parameter is BluetoothGamepad { CanPair: true });
        UnpairCommand = new AsyncParameterCommand(
            parameter => parameter is BluetoothGamepad device ? UnpairAsync(device) : Task.CompletedTask,
            parameter => parameter is BluetoothGamepad { CanUnpair: true });

        _bluetoothService.DeviceAdded += OnDeviceAdded;
        _bluetoothService.DeviceRemoved += OnDeviceRemoved;
        _bluetoothService.DeviceUpdated += OnDeviceUpdated;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _bluetoothService.StartWatcherAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsRefreshing = true;
        try
        {
            IReadOnlyList<BluetoothGamepad> devices = await _bluetoothService.EnumerateAsync(cancellationToken);

            Devices.Clear();
            foreach (BluetoothGamepad device in devices)
            {
                Devices.Add(device);
            }

            ApplyFilter();
        }
        catch (Exception exception)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Error,
                "Bluetooth LE",
                "Error al enumerar dispositivos Bluetooth",
                exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task PairAsync(BluetoothGamepad device, CancellationToken cancellationToken = default)
    {
        bool success = await _bluetoothService.PairAsync(device.Id, cancellationToken);

        if (success)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Bluetooth LE",
                $"Dispositivo emparejado: {device.Name}");
        }
        else
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Bluetooth LE",
                $"No se pudo emparejar el dispositivo: {device.Name}");
        }
    }

    private async Task UnpairAsync(BluetoothGamepad device, CancellationToken cancellationToken = default)
    {
        bool success = await _bluetoothService.UnpairAsync(device.Id, cancellationToken);

        if (success)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Bluetooth LE",
                $"Dispositivo desemparejado: {device.Name}");
        }
        else
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Bluetooth LE",
                $"No se pudo desemparejar el dispositivo: {device.Name}");
        }
    }

    private void ApplyFilter()
    {
        FilteredDevices.Clear();

        foreach (BluetoothGamepad device in Devices)
        {
            if (ShowAllDevices || device.IsRecognizedGamepad)
            {
                FilteredDevices.Add(device);
            }
        }
    }

    private void OnDeviceAdded(object? sender, BluetoothGamepad device)
    {
        Devices.Add(device);
        ApplyFilter();
    }

    private void OnDeviceRemoved(object? sender, BluetoothGamepad device)
    {
        Devices.Remove(device);
        FilteredDevices.Remove(device);
    }

    private void OnDeviceUpdated(object? sender, BluetoothGamepad device)
    {
        ApplyFilter();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _bluetoothService.DeviceAdded -= OnDeviceAdded;
        _bluetoothService.DeviceRemoved -= OnDeviceRemoved;
        _bluetoothService.DeviceUpdated -= OnDeviceUpdated;
        _bluetoothService.Dispose();

        Devices.Clear();
        FilteredDevices.Clear();
        _disposed = true;
    }

    public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class AsyncParameterCommand(
        Func<object?, Task> execute,
        Func<object?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute(parameter);
        public async void Execute(object? parameter) => await execute(parameter);

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
