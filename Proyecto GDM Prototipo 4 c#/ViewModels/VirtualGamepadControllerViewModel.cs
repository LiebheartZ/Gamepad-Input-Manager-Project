using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using GDM.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace GDM.ViewModels;

public sealed record LedModeOption(string Value, string Label);

public sealed class LedColorSlot : INotifyPropertyChanged
{
    private readonly Func<LedColorSlot, Task> _pickColor;
    private string _colorHex = "FFFFFF";
    private Brush _preview = LedColorSlot.BrushFromHex("FFFFFF");

    public LedColorSlot(int playerNumber, string colorHex, Func<LedColorSlot, Task> pickColor)
    {
        PlayerNumber = playerNumber;
        _pickColor = pickColor;
        SetColor(colorHex);
        PickCommand = new SlotAsyncCommand(() => _pickColor(this));
    }

    public int PlayerNumber { get; }
    public string Label => $"Jugador {PlayerNumber}";
    public string ColorHex => _colorHex;
    public Brush Preview => _preview;
    public ICommand PickCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetColor(string colorHex)
    {
        string normalized = ControllerLedColor.NormalizeHex(colorHex) ?? "FFFFFF";
        if (_colorHex == normalized)
        {
            return;
        }

        _colorHex = normalized;
        _preview = BrushFromHex(normalized);
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(Preview));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    internal static Brush BrushFromHex(string hex)
    {
        ControllerLedColor.TryParse(hex, out byte r, out byte g, out byte b);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

#pragma warning disable CS0067
    private sealed class SlotAsyncCommand(Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await execute();
    }
#pragma warning restore CS0067
}

public sealed class VirtualGamepadControllerViewModel : INotifyPropertyChanged
{
    private readonly Func<VirtualGamepadControllerViewModel, Task> _start;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _stop;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _toggleAutoStart;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _toggleAutoHide;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _toggleKeepHiddenOnExit;
    private readonly Func<VirtualGamepadControllerViewModel, ControllerLedSettings, Task> _saveLedSettings;
    private readonly AsyncCommand _startCommand;
    private readonly AsyncCommand _stopCommand;
    private readonly AsyncCommand _toggleAutoStartCommand;
    private readonly AsyncCommand _toggleAutoHideCommand;
    private readonly AsyncCommand _toggleKeepHiddenOnExitCommand;
    private readonly AsyncCommand _pickLedManualColorCommand;
    private readonly List<LedColorSlot> _ledPlayerColorSlots = [];
    private ControllerRecord _record;
    private ConnectedGamepad? _connectedDevice;
    private string _profileName = "Sin perfil";
    private string _virtualGamepadTypeName = "Xbox 360";
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
    public string VirtualGamepadTypeName => _virtualGamepadTypeName;
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

    public IReadOnlyList<LedModeOption> LedModeOptions { get; } =
    [
        new LedModeOption(ControllerLedModes.Off, "Desactivado"),
        new LedModeOption(ControllerLedModes.Player, "Color por jugador"),
        new LedModeOption(ControllerLedModes.Manual, "Color manual"),
        new LedModeOption(ControllerLedModes.VirtualOutput, "Según mando virtual")
    ];

    public IReadOnlyList<int> LedPlayerNumberOptions { get; } =
        [ControllerLedSettings.MinPlayerNumber, 2, 3, ControllerLedSettings.MaxPlayerNumber];
    public IReadOnlyList<LedColorSlot> LedPlayerColorSlots => _ledPlayerColorSlots;
    public ICommand PickLedManualColorCommand => _pickLedManualColorCommand;

    private ControllerLedSettings Led =>
        _record.Settings.Led ?? ControllerLedSettings.Default;

    public string LedMode
    {
        get => Led.Mode;
        set => SetLedMode(value);
    }

    public int LedBrightness
    {
        get => Led.Brightness;
        set => SetLedBrightness(value);
    }

    public int LedPlayerNumber
    {
        get => Led.PlayerNumber;
        set => SetLedPlayerNumber(value);
    }
    public string LedManualColorHex => Led.ManualColor ?? "FFFFFF";
    public Brush LedManualColorPreview { get; private set; } = LedColorSlot.BrushFromHex("FFFFFF");
    public bool HasLedSupport { get; private set; }
    public string LedSupportText { get; private set; } =
        "Conecta el mando físico para consultar el soporte de LED.";

    public VirtualGamepadControllerViewModel(
        ControllerRecord record,
        Func<VirtualGamepadControllerViewModel, Task> start,
        Func<VirtualGamepadControllerViewModel, Task> stop,
        Func<VirtualGamepadControllerViewModel, Task> toggleAutoStart,
        Func<VirtualGamepadControllerViewModel, Task> toggleAutoHide,
        Func<VirtualGamepadControllerViewModel, Task> toggleKeepHiddenOnExit,
        Func<VirtualGamepadControllerViewModel, ControllerLedSettings, Task> saveLedSettings)
    {
        _record = record;
        _start = start;
        _stop = stop;
        _toggleAutoStart = toggleAutoStart;
        _toggleAutoHide = toggleAutoHide;
        _toggleKeepHiddenOnExit = toggleKeepHiddenOnExit;
        _saveLedSettings = saveLedSettings;
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
        _pickLedManualColorCommand = new AsyncCommand(
            _ => PickManualColorAsync(),
            () => true);
        for (int player = ControllerLedSettings.MinPlayerNumber;
             player <= ControllerLedSettings.MaxPlayerNumber;
             player++)
        {
            int playerNumber = player;
            _ledPlayerColorSlots.Add(new LedColorSlot(
                playerNumber,
                Led.PlayerColors.GetValueOrDefault(playerNumber, ControllerLedSettings.DefaultPlayerColors()[playerNumber]),
                PickPlayerColorAsync));
        }

        RefreshLedProperties();
    }

    public void Update(
        ControllerRecord record,
        ConnectedGamepad? connectedDevice,
        string profileName,
        bool isRunning,
        string? virtualGamepadType = null)
    {
        bool wasConnected = _connectedDevice is not null;
        _record = record;
        _connectedDevice = connectedDevice;
        _profileName = profileName;
        _virtualGamepadTypeName = VirtualGamepadTypes.DisplayName(virtualGamepadType);
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
        OnPropertyChanged(nameof(VirtualGamepadTypeName));
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
        RefreshLedProperties();
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
        RefreshLedProperties();
    }

    public void SetLedMode(string value)
    {
        string normalized = ControllerLedModes.Normalize(value);
        if (Led.Mode == normalized)
        {
            return;
        }

        _ = _saveLedSettings(this, Led with { Mode = normalized });
    }

    public void SetLedBrightness(int value)
    {
        int clamped = Math.Clamp(value, ControllerLedSettings.MinBrightness, ControllerLedSettings.MaxBrightness);
        if (Led.Brightness == clamped)
        {
            return;
        }

        _ = _saveLedSettings(this, Led with { Brightness = clamped });
    }

    public void SetLedPlayerNumber(int value)
    {
        int clamped = Math.Clamp(value, ControllerLedSettings.MinPlayerNumber, ControllerLedSettings.MaxPlayerNumber);
        if (Led.PlayerNumber == clamped)
        {
            return;
        }

        _ = _saveLedSettings(this, Led with { PlayerNumber = clamped });
    }

    public void SetLedManualColor(string hex)
    {
        string? normalized = ControllerLedColor.NormalizeHex(hex);
        if (normalized is null || Led.ManualColor == normalized)
        {
            return;
        }

        _ = _saveLedSettings(this, Led with { ManualColor = normalized });
    }

    public void SetLedPlayerColor(int playerNumber, string hex)
    {
        string? normalized = ControllerLedColor.NormalizeHex(hex);
        if (normalized is null ||
            playerNumber is < ControllerLedSettings.MinPlayerNumber or > ControllerLedSettings.MaxPlayerNumber)
        {
            return;
        }

        var colors = new Dictionary<int, string>(Led.PlayerColors)
        {
            [playerNumber] = normalized
        };
        _ = _saveLedSettings(this, Led with { PlayerColors = colors });
    }

    private Task PickManualColorAsync()
    {
        string? picked = PickColor(LedManualColorHex);
        if (picked is not null)
        {
            SetLedManualColor(picked);
        }

        return Task.CompletedTask;
    }

    private Task PickPlayerColorAsync(LedColorSlot slot)
    {
        string? picked = PickColor(slot.ColorHex);
        if (picked is not null)
        {
            SetLedPlayerColor(slot.PlayerNumber, picked);
        }

        return Task.CompletedTask;
    }

    private static string? PickColor(string? currentHex)
    {
        var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true
        };
        if (ControllerLedColor.TryParse(currentHex, out byte r, out byte g, out byte b))
        {
            dialog.Color = System.Drawing.Color.FromArgb(r, g, b);
        }

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? ControllerLedColor.ToHex(dialog.Color)
            : null;
    }

    private void RefreshLedProperties()
    {
        ControllerLedSettings led = Led;
        LedManualColorPreview = LedColorSlot.BrushFromHex(led.ManualColor ?? "FFFFFF");
        foreach (LedColorSlot slot in _ledPlayerColorSlots)
        {
            slot.SetColor(led.PlayerColors.GetValueOrDefault(
                slot.PlayerNumber,
                ControllerLedSettings.DefaultPlayerColors()[slot.PlayerNumber]));
        }

        if (_connectedDevice is null)
        {
            HasLedSupport = false;
            LedSupportText = "Conecta el mando físico para consultar el soporte de LED.";
        }
        else if (_connectedDevice.IsVirtualGamepad)
        {
            HasLedSupport = false;
            LedSupportText = "Los mandos virtuales no tienen LED físicos que controlar.";
        }
        else if (SonyControllerLedSupport.TryGetKind(
                     _connectedDevice.VendorId,
                     _connectedDevice.ProductId,
                     out SonyControllerLedKind kind))
        {
            HasLedSupport = true;
            LedSupportText = $"LED compatible: {SonyControllerLedSupport.Describe(kind)} (funciona por USB y Bluetooth).";
        }
        else
        {
            HasLedSupport = false;
            LedSupportText = "Este mando no es un DualShock 4 ni un DualSense; no se puede controlar su LED.";
        }

        OnPropertyChanged(nameof(LedMode));
        OnPropertyChanged(nameof(LedBrightness));
        OnPropertyChanged(nameof(LedPlayerNumber));
        OnPropertyChanged(nameof(LedManualColorHex));
        OnPropertyChanged(nameof(LedManualColorPreview));
        OnPropertyChanged(nameof(HasLedSupport));
        OnPropertyChanged(nameof(LedSupportText));
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
