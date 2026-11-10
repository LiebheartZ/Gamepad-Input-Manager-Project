using System.ComponentModel;
using System.Windows.Input;
using GDM.Models;

namespace GDM.ViewModels;

public sealed class VirtualGamepadControllerViewModel : INotifyPropertyChanged
{
    private readonly Func<VirtualGamepadControllerViewModel, Task> _start;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _stop;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _toggleAutoStart;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _toggleAutoHide;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _toggleKeepHiddenOnExit;
    private readonly AsyncCommand _startCommand;
    private readonly AsyncCommand _stopCommand;
    private readonly AsyncCommand _toggleAutoStartCommand;
    private readonly AsyncCommand _toggleAutoHideCommand;
    private readonly AsyncCommand _toggleKeepHiddenOnExitCommand;
    private ControllerRecord _record;
    private ConnectedGamepad? _connectedDevice;
    private string _profileName = "Sin perfil";
    private string _statusMessage = "Detenida";
    private bool _isRunning;
    private bool _isStarting;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ControllerRecord Record => _record;
    public ConnectedGamepad? ConnectedDevice => _connectedDevice;
    public string DisplayName => _record.DisplayName;
    public string ConnectionStatus => _connectedDevice is null
        ? "Mando físico desconectado"
        : "Mando físico conectado";
    public string ProfileName => _profileName;
    public string StatusMessage => _statusMessage;
    public bool IsRunning => _isRunning;
    public bool IsStarting => _isStarting;
    public bool AutoStartOnConnect => _record.Settings.AutoStartVirtualGamepad;
    public bool AutoHideOnConnect => _record.Settings.AutoHidePhysicalGamepad;
    public bool KeepHiddenOnExit => _record.Settings.KeepHiddenOnExit;
    public bool CanStart => _connectedDevice is not null && !_isRunning && !_isStarting;
    public bool CanStop => _isRunning && !_isStarting;
    public ICommand StartCommand => _startCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand ToggleAutoStartCommand => _toggleAutoStartCommand;
    public ICommand ToggleAutoHideCommand => _toggleAutoHideCommand;
    public ICommand ToggleKeepHiddenOnExitCommand => _toggleKeepHiddenOnExitCommand;

    public VirtualGamepadControllerViewModel(
        ControllerRecord record,
        Func<VirtualGamepadControllerViewModel, Task> start,
        Func<VirtualGamepadControllerViewModel, Task> stop,
        Func<VirtualGamepadControllerViewModel, Task> toggleAutoStart,
        Func<VirtualGamepadControllerViewModel, Task> toggleAutoHide,
        Func<VirtualGamepadControllerViewModel, Task> toggleKeepHiddenOnExit)
    {
        _record = record;
        _start = start;
        _stop = stop;
        _toggleAutoStart = toggleAutoStart;
        _toggleAutoHide = toggleAutoHide;
        _toggleKeepHiddenOnExit = toggleKeepHiddenOnExit;
        _startCommand = new AsyncCommand(_start, () => CanStart);
        _stopCommand = new AsyncCommand(_stop, () => CanStop);
        _toggleAutoStartCommand = new AsyncCommand(
            _toggleAutoStart,
            () => !_isStarting);
        _toggleAutoHideCommand = new AsyncCommand(
            _toggleAutoHide,
            () => !_isStarting);
        _toggleKeepHiddenOnExitCommand = new AsyncCommand(
            _toggleKeepHiddenOnExit,
            () => !_isStarting);
    }

    public void Update(
        ControllerRecord record,
        ConnectedGamepad? connectedDevice,
        string profileName,
        bool isRunning)
    {
        bool wasConnected = _connectedDevice is not null;
        _record = record;
        _connectedDevice = connectedDevice;
        _profileName = profileName;
        _isRunning = isRunning;
        if (connectedDevice is null && wasConnected && isRunning)
        {
            _statusMessage =
                "Se desconectó el mando físico; se detendrá su salida virtual.";
        }

        OnPropertyChanged(nameof(Record));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ConnectedDevice));
        OnPropertyChanged(nameof(ConnectionStatus));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(AutoStartOnConnect));
        OnPropertyChanged(nameof(AutoHideOnConnect));
        OnPropertyChanged(nameof(KeepHiddenOnExit));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _toggleAutoStartCommand.NotifyCanExecuteChanged();
        _toggleAutoHideCommand.NotifyCanExecuteChanged();
        _toggleKeepHiddenOnExitCommand.NotifyCanExecuteChanged();
    }

    public void SetStarting(bool isStarting)
    {
        _isStarting = isStarting;
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _toggleAutoStartCommand.NotifyCanExecuteChanged();
        _toggleAutoHideCommand.NotifyCanExecuteChanged();
        _toggleKeepHiddenOnExitCommand.NotifyCanExecuteChanged();
    }

    public void SetStatus(string status)
    {
        _statusMessage = status;
        OnPropertyChanged(nameof(StatusMessage));
    }

    public void UpdateRecord(ControllerRecord record)
    {
        _record = record;
        OnPropertyChanged(nameof(Record));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AutoStartOnConnect));
        OnPropertyChanged(nameof(AutoHideOnConnect));
        OnPropertyChanged(nameof(KeepHiddenOnExit));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(
        Func<VirtualGamepadControllerViewModel, Task> execute,
        Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute((VirtualGamepadControllerViewModel)parameter!);
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
