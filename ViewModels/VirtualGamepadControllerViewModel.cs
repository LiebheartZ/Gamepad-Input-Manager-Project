using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using GDM.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace GDM.ViewModels;

public sealed record LedModeOption(string Value, string Label);

public sealed record PowerOffIdleOption(int Seconds, string Label);
public sealed record BatteryAlertLevelOption(int Level, string Label);
public sealed record TriStateOption(int Value, string Label);

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
    private readonly Func<VirtualGamepadControllerViewModel, Task> _powerOff;
    private readonly Func<VirtualGamepadControllerViewModel, int, Task> _savePowerOffSettings;
    private readonly Func<VirtualGamepadControllerViewModel, ControllerLedSettings, Task> _saveLedSettings;
    private readonly Func<VirtualGamepadControllerViewModel, bool, Task> _saveVibrationSettings;
    private readonly Func<VirtualGamepadControllerViewModel, int, Task> _savePollIntervalSettings;
    private readonly Func<VirtualGamepadControllerViewModel, bool, Task> _saveMotionSettings;
    private readonly Func<VirtualGamepadControllerViewModel, int, Task> _saveMotionIntervalSettings;
    private readonly Func<VirtualGamepadControllerViewModel, Task> _calibrateMotion;
    private readonly Func<VirtualGamepadControllerViewModel, bool, bool, bool, Task> _saveMotionInvertSettings;
    private readonly Func<VirtualGamepadControllerViewModel, ControllerSettings, Task> _saveBatterySettings;
    private readonly Func<VirtualGamepadControllerViewModel, string, bool?, Task> _saveBoolOverride;
    private readonly Func<VirtualGamepadControllerViewModel, string, object?, Task> _saveOverrideValue;
    private readonly Func<VirtualGamepadControllerViewModel, string?, Task> _resetSettingOverrides;
    private readonly Func<ControllerRecord, ControllerSettings> _effectiveSettings;
    private readonly AsyncCommand _toggleVibrationCommand;
    private readonly AsyncCommand _toggleMotionCommand;
    private readonly AsyncCommand _calibrateMotionCommand;
    private readonly AsyncCommand _startCommand;
    private readonly AsyncCommand _stopCommand;
    private readonly AsyncCommand _toggleAutoStartCommand;
    private readonly AsyncCommand _toggleAutoHideCommand;
    private readonly AsyncCommand _powerOffCommand;
    private readonly AsyncCommand _pickLedManualColorCommand;
    private readonly AsyncCommand _pickBatteryColorCommand;
    private readonly List<LedColorSlot> _ledPlayerColorSlots = [];
    private ControllerRecord _record;
    private ConnectedGamepad? _connectedDevice;
    private string _profileName = "Perfil por mando";
    private string _virtualGamepadTypeName = "Xbox 360";
    private string _statusMessage = "Detenida";
    private bool _isRunning;
    private bool _isStarting;
    private bool _isTemporary;

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
    public bool IsTemporary => _isTemporary;
    public bool IsGlobalPresetEditor => false;
    public bool AutoStartOnConnect => Effective.AutoStartVirtualGamepad;
    public bool AutoHideOnConnect => Effective.AutoHidePhysicalGamepad;
    public bool IsVibrationOverridden => _record.SettingOverrides.VibrationEnabled.HasValue;
    public bool IsAutoStartOverridden => _record.SettingOverrides.AutoStartVirtualGamepad.HasValue;
    public bool IsAutoHideOverridden => _record.SettingOverrides.AutoHidePhysicalGamepad.HasValue;
    public bool IsLedOverridden => _record.SettingOverrides.Led is not null;
    public bool IsPowerOffOverridden => _record.SettingOverrides.PowerOffIdleSeconds.HasValue;
    public bool IsInputPollOverridden => _record.SettingOverrides.InputPollIntervalMs.HasValue;
    public bool IsMotionOverridden => _record.SettingOverrides.MotionEnabled.HasValue;
    public bool IsMotionIntervalOverridden => _record.SettingOverrides.MotionPollIntervalMs.HasValue;
    public bool IsBatteryOverridden =>
        _record.SettingOverrides.BatteryAlertEnabled.HasValue ||
        _record.SettingOverrides.BatteryAlertLevel.HasValue ||
        _record.SettingOverrides.BatteryAlertColorEnabled.HasValue ||
        _record.SettingOverrides.BatteryAlertColor is not null ||
        _record.SettingOverrides.BatteryAlertEffectEnabled.HasValue ||
        _record.SettingOverrides.BatteryAlertEffect is not null ||
        _record.SettingOverrides.BatteryNotifyEnabled.HasValue ||
        _record.SettingOverrides.BatterySoundEnabled.HasValue ||
        _record.SettingOverrides.ChargingLedEffect is not null ||
        _record.SettingOverrides.LedEffectColorIntensity.HasValue;
    public bool IsIgnoreExternalOverridden =>
        _record.SettingOverrides.IgnoreExternalColorSignals.HasValue;
    public bool IsProfileLoadVibrationOverridden =>
        _record.SettingOverrides.ProfileLoadVibrationEnabled.HasValue;
    public bool HasSettingOverrides =>
        IsVibrationOverridden || IsAutoStartOverridden || IsAutoHideOverridden ||
        IsLedOverridden || IsPowerOffOverridden || IsInputPollOverridden ||
        IsMotionOverridden || IsMotionIntervalOverridden || IsBatteryOverridden ||
        IsIgnoreExternalOverridden || IsKeepHiddenOverridden ||
        IsProfileLoadVibrationOverridden;
    public bool UsesGlobalDefaults => !HasSettingOverrides;

    private const int InheritNumber = -1;
    private const string InheritToken = "__global__";

    public IReadOnlyList<TriStateOption> TriStateOptions { get; } =
    [
        new TriStateOption(0, "Ajuste global"),
        new TriStateOption(1, "Encendido"),
        new TriStateOption(2, "Apagado")
    ];

    private static int OverrideState(bool? value) => value switch
    {
        null => 0,
        true => 1,
        _ => 2
    };

    private void SetBoolOverride(string key, int state) =>
        _ = _saveBoolOverride(
            this,
            key,
            state switch
            {
                1 => true,
                2 => false,
                _ => (bool?)null
            });

    public int VibrationState
    {
        get => OverrideState(_record.SettingOverrides.VibrationEnabled);
        set => SetBoolOverride("vibration", value);
    }

    public int AutoStartState
    {
        get => OverrideState(_record.SettingOverrides.AutoStartVirtualGamepad);
        set => SetBoolOverride("autoStart", value);
    }

    public int AutoHideState
    {
        get => OverrideState(_record.SettingOverrides.AutoHidePhysicalGamepad);
        set => SetBoolOverride("autoHide", value);
    }

    public int MotionState
    {
        get => OverrideState(_record.SettingOverrides.MotionEnabled);
        set => SetBoolOverride("motion", value);
    }

    public int BatteryAlertState
    {
        get => OverrideState(_record.SettingOverrides.BatteryAlertEnabled);
        set => SetBoolOverride("batteryAlert", value);
    }

    public int BatteryNotifyState
    {
        get => OverrideState(_record.SettingOverrides.BatteryNotifyEnabled);
        set => SetBoolOverride("batteryNotify", value);
    }

    public int BatterySoundState
    {
        get => OverrideState(_record.SettingOverrides.BatterySoundEnabled);
        set => SetBoolOverride("batterySound", value);
    }

    public int BatteryAlertColorState
    {
        get => OverrideState(_record.SettingOverrides.BatteryAlertColorEnabled);
        set => SetBoolOverride("batteryColor", value);
    }

    public int BatteryAlertEffectState
    {
        get => OverrideState(_record.SettingOverrides.BatteryAlertEffectEnabled);
        set => SetBoolOverride("batteryEffect", value);
    }

    public int IgnoreExternalState
    {
        get => OverrideState(_record.SettingOverrides.IgnoreExternalColorSignals);
        set => SetBoolOverride("ignoreExternal", value);
    }

    public int KeepHiddenState
    {
        get => OverrideState(_record.SettingOverrides.KeepHiddenOnExit);
        set => SetBoolOverride("keepHidden", value);
    }

    public int ProfileLoadVibrationState
    {
        get => OverrideState(_record.SettingOverrides.ProfileLoadVibrationEnabled);
        set => SetBoolOverride("profileVibration", value);
    }

    public bool IsKeepHiddenOverridden => _record.SettingOverrides.KeepHiddenOnExit.HasValue;

    private void SetInheritableValue(string key, object? value) =>
        _ = _saveOverrideValue(this, key, value);

    public int PowerOffIdleSelection
    {
        get => _record.SettingOverrides.PowerOffIdleSeconds ?? InheritNumber;
        set
        {
            int? normalized = value == InheritNumber
                ? null
                : ControllerPowerOff.Normalize(value);
            if (normalized == _record.SettingOverrides.PowerOffIdleSeconds)
            {
                return;
            }

            SetInheritableValue("powerOff", normalized);
        }
    }

    public int InputPollIntervalSelection
    {
        get => _record.SettingOverrides.InputPollIntervalMs ?? InheritNumber;
        set
        {
            int? normalized = value == InheritNumber
                ? null
                : value <= 0 ? 0 : Math.Clamp(value, 4, 100);
            if (normalized == _record.SettingOverrides.InputPollIntervalMs)
            {
                return;
            }

            SetInheritableValue("inputPoll", normalized);
        }
    }

    public int MotionPollIntervalSelection
    {
        get => _record.SettingOverrides.MotionPollIntervalMs ?? InheritNumber;
        set
        {
            int? normalized = value == InheritNumber
                ? null
                : value <= 0 ? 0 : Math.Clamp(value, 4, 100);
            if (normalized == _record.SettingOverrides.MotionPollIntervalMs)
            {
                return;
            }

            SetInheritableValue("motionInterval", normalized);
        }
    }

    public int BatteryAlertLevelSelection
    {
        get => _record.SettingOverrides.BatteryAlertLevel ?? InheritNumber;
        set
        {
            int? normalized = value == InheritNumber
                ? null
                : BatteryAlertLevels.Normalize(value);
            if (normalized == _record.SettingOverrides.BatteryAlertLevel)
            {
                return;
            }

            SetInheritableValue("batteryLevel", normalized);
        }
    }

    public string BatteryAlertEffectSelection
    {
        get => _record.SettingOverrides.BatteryAlertEffect ?? InheritToken;
        set
        {
            string? normalized = value == InheritToken
                ? null
                : BatteryAlertEffects.Normalize(value);
            if (normalized == _record.SettingOverrides.BatteryAlertEffect)
            {
                return;
            }

            SetInheritableValue("batteryEffectValue", normalized);
        }
    }

    public string ChargingLedEffectSelection
    {
        get => _record.SettingOverrides.ChargingLedEffect ?? InheritToken;
        set
        {
            string? normalized = value == InheritToken
                ? null
                : ChargingLedEffects.Normalize(value);
            if (normalized == _record.SettingOverrides.ChargingLedEffect)
            {
                return;
            }

            SetInheritableValue("chargingEffect", normalized);
        }
    }

    public int LedColorIntensityState
    {
        get => OverrideState(_record.SettingOverrides.LedEffectColorIntensity);
        set => SetBoolOverride("ledColorIntensity", value);
    }

    public bool LedInheritsGlobal
    {
        get => _record.SettingOverrides.Led is null;
        set
        {
            if (value == LedInheritsGlobal)
            {
                return;
            }

            if (value)
            {
                _ = _resetSettingOverrides(this, "led");
            }
            else
            {
                _ = _saveLedSettings(this, Led);
            }
        }
    }

    public bool BatteryAlertColorInherits
    {
        get => _record.SettingOverrides.BatteryAlertColor is null;
        set
        {
            if (value == BatteryAlertColorInherits)
            {
                return;
            }

            SetInheritableValue("batteryColorHex", value ? null : BatteryAlertColorHex);
        }
    }
    public bool CanStart => _connectedDevice is not null && !_isRunning && !_isStarting;
    public bool CanStop => _isRunning && !_isStarting;
    public ICommand StartCommand => _startCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand ToggleAutoStartCommand => _toggleAutoStartCommand;
    public ICommand ToggleAutoHideCommand => _toggleAutoHideCommand;
    public ICommand PowerOffCommand => _powerOffCommand;
    public ICommand ToggleVibrationCommand => _toggleVibrationCommand;
    public ICommand ToggleMotionCommand => _toggleMotionCommand;
    public ICommand CalibrateMotionCommand => _calibrateMotionCommand;
    public ICommand ResetSettingCommand { get; }
    public bool VibrationEnabled => Effective.VibrationEnabled;

    public IReadOnlyList<SettingsViewModel.PollIntervalOption> InputPollIntervalOptions { get; } =
    [
        new(-1, "Ajuste global"),
        new(0, "Predeterminado"),
        new(4, "4 ms - respuesta máxima"),
        new(8, "8 ms - alta respuesta"),
        new(16, "16 ms - equilibrado"),
        new(33, "33 ms - bajo consumo"),
        new(50, "50 ms - mínimo consumo")
    ];

    private ControllerSettings Effective => _effectiveSettings(_record);

    public int InputPollIntervalMs
    {
        get => Effective.InputPollIntervalMs;
        set
        {
            int normalized = value <= 0 ? 0 : Math.Clamp(value, 4, 100);
            if (normalized == Effective.InputPollIntervalMs)
            {
                return;
            }

            _ = _savePollIntervalSettings(this, normalized);
        }
    }

    public IReadOnlyList<SettingsViewModel.PollIntervalOption> MotionPollIntervalOptions { get; } =
    [
        new(-1, "Ajuste global"),
        new(0, "Predeterminado"),
        new(4, "4 ms"),
        new(8, "8 ms"),
        new(16, "16 ms"),
        new(33, "33 ms"),
        new(50, "50 ms")
    ];

    public bool MotionEnabled
    {
        get => Effective.MotionEnabled;
        set
        {
            if (value == Effective.MotionEnabled)
            {
                return;
            }

            _ = _saveMotionSettings(this, value);
        }
    }

    public int MotionPollIntervalMs
    {
        get => Effective.MotionPollIntervalMs;
        set
        {
            int normalized = value <= 0 ? 0 : Math.Clamp(value, 4, 100);
            if (normalized == Effective.MotionPollIntervalMs)
            {
                return;
            }

            _ = _saveMotionIntervalSettings(this, normalized);
        }
    }

    public bool MotionCalibrated => _record.Settings.MotionCalibrated;
    public string MotionCalibrationText => _record.Settings.MotionCalibrated
        ? "Calibración del giróscopo: aplicada."
        : "Calibración del giróscopo: sin calibrar.";

    public bool MotionInvertGyroPitch
    {
        get => _record.Settings.MotionInvertGyroPitch;
        set => _ = _saveMotionInvertSettings(this, value, _record.Settings.MotionInvertGyroYaw, _record.Settings.MotionInvertGyroRoll);
    }

    public bool MotionInvertGyroYaw
    {
        get => _record.Settings.MotionInvertGyroYaw;
        set => _ = _saveMotionInvertSettings(this, _record.Settings.MotionInvertGyroPitch, value, _record.Settings.MotionInvertGyroRoll);
    }

    public bool MotionInvertGyroRoll
    {
        get => _record.Settings.MotionInvertGyroRoll;
        set => _ = _saveMotionInvertSettings(this, _record.Settings.MotionInvertGyroPitch, _record.Settings.MotionInvertGyroYaw, value);
    }

    public IReadOnlyList<BatteryAlertLevelOption> BatteryAlertLevelOptions { get; } =
    [
        new BatteryAlertLevelOption(-1, "Ajuste global"),
        new BatteryAlertLevelOption((int)GamepadBatteryLevel.Empty, "Vacío o menos"),
        new BatteryAlertLevelOption((int)GamepadBatteryLevel.Low, "Baja o menos"),
        new BatteryAlertLevelOption((int)GamepadBatteryLevel.Medium, "Media o menos")
    ];
    public IReadOnlyList<LedModeOption> BatteryAlertEffectOptions { get; } =
    [
        new LedModeOption(InheritToken, "Ajuste global"),
        new LedModeOption(BatteryAlertEffects.Blink, "Parpadeo (pulsos)"),
        new LedModeOption(BatteryAlertEffects.Fade, "Desvanecido")
    ];
    public IReadOnlyList<LedModeOption> ChargingLedEffectOptions { get; } =
    [
        new LedModeOption(InheritToken, "Ajuste global"),
        new LedModeOption(ChargingLedEffects.None, "Ninguno"),
        new LedModeOption(ChargingLedEffects.Blink, "Parpadeo (pulsos)"),
        new LedModeOption(ChargingLedEffects.Fade, "Desvanecido")
    ];
    public string BatteryInfoText { get; private set; } =
        "Conecta el mando físico para ver su batería.";
    public ICommand PickBatteryColorCommand => _pickBatteryColorCommand;

    public bool BatteryAlertEnabled
    {
        get => Effective.BatteryAlertEnabled;
        set => SetBoolOverride("batteryAlert", OverrideState(value));
    }

    public int BatteryAlertLevel
    {
        get => BatteryAlertLevels.Normalize(Effective.BatteryAlertLevel);
        set => SetInheritableValue("batteryLevel", BatteryAlertLevels.Normalize(value));
    }

    public bool BatteryAlertColorEnabled
    {
        get => Effective.BatteryAlertColorEnabled;
        set => SetBoolOverride("batteryColor", OverrideState(value));
    }

    public string BatteryAlertColorHex =>
        ControllerLedColor.NormalizeHex(Effective.BatteryAlertColor) ?? "FF453A";
    public Brush BatteryAlertColorPreview { get; private set; } =
        LedColorSlot.BrushFromHex("FF453A");

    public bool BatteryAlertEffectEnabled
    {
        get => Effective.BatteryAlertEffectEnabled;
        set => SetBoolOverride("batteryEffect", OverrideState(value));
    }

    public string BatteryAlertEffect
    {
        get => BatteryAlertEffects.Normalize(Effective.BatteryAlertEffect);
        set => SetInheritableValue("batteryEffectValue", BatteryAlertEffects.Normalize(value));
    }

    public bool BatteryNotifyEnabled
    {
        get => Effective.BatteryNotifyEnabled;
        set => SetBoolOverride("batteryNotify", OverrideState(value));
    }

    public bool BatterySoundEnabled
    {
        get => Effective.BatterySoundEnabled;
        set => SetBoolOverride("batterySound", OverrideState(value));
    }

    public bool IgnoreExternalColorSignals
    {
        get => Effective.IgnoreExternalColorSignals;
        set
        {
            if (value == Effective.IgnoreExternalColorSignals)
            {
                return;
            }

            _ = _saveBoolOverride(this, "ignoreExternal", value);
            OnPropertyChanged(nameof(IgnoreExternalColorSignals));
        }
    }

    public bool RestartPhysicalDeviceOnVirtualStart
    {
        get => Effective.RestartPhysicalDeviceOnVirtualStart;
        set
        {
            if (value == Effective.RestartPhysicalDeviceOnVirtualStart)
            {
                return;
            }

            _ = _saveBoolOverride(this, "restartDevice", value);
            OnPropertyChanged(nameof(RestartPhysicalDeviceOnVirtualStart));
        }
    }

    private void SaveBattery(Func<ControllerSettings, ControllerSettings> mutate) =>
        _ = _saveBatterySettings(this, mutate(Effective));
    public IReadOnlyList<LedModeOption> LedModeOptions { get; } =
    [
        new LedModeOption(ControllerLedModes.Off, "Desactivado"),
        new LedModeOption(ControllerLedModes.Player, "Color por jugador"),
        new LedModeOption(ControllerLedModes.Manual, "Color manual"),
        new LedModeOption(ControllerLedModes.VirtualOutput, "Según mando virtual")
    ];

    public IReadOnlyList<int> LedPlayerNumberOptions { get; } =
        [ControllerLedSettings.MinPlayerNumber, 2, 3, ControllerLedSettings.MaxPlayerNumber];
    public IReadOnlyList<PowerOffIdleOption> PowerOffIdleOptions { get; } =
    [
        new(-1, "Ajuste global"),
        new(0, "Desactivado"),
        new(60, "1 minuto"),
        new(300, "5 minutos"),
        new(900, "15 minutos"),
        new(1800, "30 minutos"),
        new(3600, "1 hora"),
        new(7200, "2 horas")
    ];
    public IReadOnlyList<LedColorSlot> LedPlayerColorSlots => _ledPlayerColorSlots;
    public ICommand PickLedManualColorCommand => _pickLedManualColorCommand;

    private ControllerLedSettings Led =>
        ControllerLedSettings.Normalize(Effective.Led);

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
    public string PowerOffSupportText { get; private set; } =
        "Conecta el mando físico para consultar si se puede apagar.";

    public VirtualGamepadControllerViewModel(
        ControllerRecord record,
        Func<VirtualGamepadControllerViewModel, Task> start,
        Func<VirtualGamepadControllerViewModel, Task> stop,
        Func<VirtualGamepadControllerViewModel, Task> toggleAutoStart,
        Func<VirtualGamepadControllerViewModel, Task> toggleAutoHide,
        Func<VirtualGamepadControllerViewModel, Task> powerOff,
        Func<VirtualGamepadControllerViewModel, int, Task> savePowerOffSettings,
        Func<VirtualGamepadControllerViewModel, ControllerLedSettings, Task> saveLedSettings,
        Func<VirtualGamepadControllerViewModel, bool, Task> saveVibrationSettings,
        Func<VirtualGamepadControllerViewModel, int, Task> savePollIntervalSettings,
        Func<VirtualGamepadControllerViewModel, bool, Task> saveMotionSettings,
        Func<VirtualGamepadControllerViewModel, int, Task> saveMotionIntervalSettings,
        Func<VirtualGamepadControllerViewModel, Task> calibrateMotion,
        Func<VirtualGamepadControllerViewModel, bool, bool, bool, Task> saveMotionInvertSettings,
        Func<VirtualGamepadControllerViewModel, ControllerSettings, Task> saveBatterySettings,
        Func<ControllerRecord, ControllerSettings> effectiveSettings,
        Func<VirtualGamepadControllerViewModel, string?, Task> resetSettingOverrides,
        Func<VirtualGamepadControllerViewModel, string, bool?, Task> saveBoolOverride,
        Func<VirtualGamepadControllerViewModel, string, object?, Task> saveOverrideValue)
    {
        _record = record;
        _start = start;
        _stop = stop;
        _toggleAutoStart = toggleAutoStart;
        _toggleAutoHide = toggleAutoHide;
        _powerOff = powerOff;
        _savePowerOffSettings = savePowerOffSettings;
        _saveLedSettings = saveLedSettings;
        _saveVibrationSettings = saveVibrationSettings;
        _savePollIntervalSettings = savePollIntervalSettings;
        _saveMotionSettings = saveMotionSettings;
        _saveMotionIntervalSettings = saveMotionIntervalSettings;
        _calibrateMotion = calibrateMotion;
        _saveMotionInvertSettings = saveMotionInvertSettings;
        _saveBatterySettings = saveBatterySettings;
        _effectiveSettings = effectiveSettings;
        _resetSettingOverrides = resetSettingOverrides;
        _saveBoolOverride = saveBoolOverride;
        _saveOverrideValue = saveOverrideValue;
        _toggleVibrationCommand = new AsyncCommand(
            _ => _saveVibrationSettings(this, !Effective.VibrationEnabled),
            () => true);
        _toggleMotionCommand = new AsyncCommand(
            _ => _saveMotionSettings(this, !Effective.MotionEnabled),
            () => true);
        _calibrateMotionCommand = new AsyncCommand(
            _calibrateMotion,
            () => true);
        _startCommand = new AsyncCommand(_start, () => CanStart);
        _stopCommand = new AsyncCommand(_stop, () => CanStop);
        _toggleAutoStartCommand = new AsyncCommand(
            _toggleAutoStart,
            () => !_isStarting);
        _toggleAutoHideCommand = new AsyncCommand(
            _toggleAutoHide,
            () => !_isStarting);
        _powerOffCommand = new AsyncCommand(_powerOff, () => !_isStarting);
        _pickLedManualColorCommand = new AsyncCommand(
            _ => PickManualColorAsync(),
            () => true);
        _pickBatteryColorCommand = new AsyncCommand(
            _ => PickBatteryColorAsync(),
            () => true);
        ResetSettingCommand = new KeyedCommand(
            key => _resetSettingOverrides(this, key));
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
        OnPropertyChanged(nameof(VibrationEnabled));
        OnPropertyChanged(nameof(PowerOffIdleSeconds));
        OnPropertyChanged(nameof(InputPollIntervalMs));
        OnPropertyChanged(nameof(MotionEnabled));
        OnPropertyChanged(nameof(MotionPollIntervalMs));
        OnPropertyChanged(nameof(MotionCalibrated));
        OnPropertyChanged(nameof(MotionCalibrationText));
        OnPropertyChanged(nameof(MotionInvertGyroPitch));
        OnPropertyChanged(nameof(MotionInvertGyroYaw));
        OnPropertyChanged(nameof(MotionInvertGyroRoll));
        NotifyOverrideProperties();
        NotifyBatteryProperties();
        BatteryInfoText = connectedDevice?.BatteryInfoText ??
            "Conecta el mando físico para ver su batería.";
        OnPropertyChanged(nameof(BatteryInfoText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        _startCommand.NotifyCanExecuteChanged();
        _stopCommand.NotifyCanExecuteChanged();
        _toggleAutoStartCommand.NotifyCanExecuteChanged();
        _toggleAutoHideCommand.NotifyCanExecuteChanged();
        _powerOffCommand.NotifyCanExecuteChanged();
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
        _powerOffCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(VibrationEnabled));
        OnPropertyChanged(nameof(PowerOffIdleSeconds));
        OnPropertyChanged(nameof(InputPollIntervalMs));
        OnPropertyChanged(nameof(MotionEnabled));
        OnPropertyChanged(nameof(MotionPollIntervalMs));
        OnPropertyChanged(nameof(MotionCalibrated));
        OnPropertyChanged(nameof(MotionCalibrationText));
        OnPropertyChanged(nameof(MotionInvertGyroPitch));
        OnPropertyChanged(nameof(MotionInvertGyroYaw));
        OnPropertyChanged(nameof(MotionInvertGyroRoll));
        NotifyOverrideProperties();
        NotifyBatteryProperties();
        RefreshLedProperties();
    }

    public void RefreshEffectiveSettings()
    {
        OnPropertyChanged(nameof(AutoStartOnConnect));
        OnPropertyChanged(nameof(AutoHideOnConnect));
        OnPropertyChanged(nameof(VibrationEnabled));
        OnPropertyChanged(nameof(PowerOffIdleSeconds));
        OnPropertyChanged(nameof(InputPollIntervalMs));
        OnPropertyChanged(nameof(MotionEnabled));
        OnPropertyChanged(nameof(MotionPollIntervalMs));
        OnPropertyChanged(nameof(MotionCalibrated));
        OnPropertyChanged(nameof(MotionCalibrationText));
        OnPropertyChanged(nameof(MotionInvertGyroPitch));
        OnPropertyChanged(nameof(MotionInvertGyroYaw));
        OnPropertyChanged(nameof(MotionInvertGyroRoll));
        NotifyOverrideProperties();
        NotifyBatteryProperties();
        RefreshLedProperties();
    }

    public void SetTemporary(bool isTemporary)
    {
        if (_isTemporary == isTemporary)
        {
            return;
        }

        _isTemporary = isTemporary;
        OnPropertyChanged(nameof(IsTemporary));
    }

    private void NotifyOverrideProperties()
    {
        OnPropertyChanged(nameof(IsVibrationOverridden));
        OnPropertyChanged(nameof(IsAutoStartOverridden));
        OnPropertyChanged(nameof(IsAutoHideOverridden));
        OnPropertyChanged(nameof(IsLedOverridden));
        OnPropertyChanged(nameof(IsPowerOffOverridden));
        OnPropertyChanged(nameof(IsInputPollOverridden));
        OnPropertyChanged(nameof(IsMotionOverridden));
        OnPropertyChanged(nameof(IsMotionIntervalOverridden));
        OnPropertyChanged(nameof(IsBatteryOverridden));
        OnPropertyChanged(nameof(IsIgnoreExternalOverridden));
        OnPropertyChanged(nameof(HasSettingOverrides));
        OnPropertyChanged(nameof(UsesGlobalDefaults));
        OnPropertyChanged(nameof(VibrationState));
        OnPropertyChanged(nameof(AutoStartState));
        OnPropertyChanged(nameof(AutoHideState));
        OnPropertyChanged(nameof(MotionState));
        OnPropertyChanged(nameof(BatteryAlertState));
        OnPropertyChanged(nameof(BatteryNotifyState));
        OnPropertyChanged(nameof(BatterySoundState));
        OnPropertyChanged(nameof(BatteryAlertColorState));
        OnPropertyChanged(nameof(BatteryAlertEffectState));
        OnPropertyChanged(nameof(IgnoreExternalState));
        OnPropertyChanged(nameof(KeepHiddenState));
        OnPropertyChanged(nameof(IsKeepHiddenOverridden));
        OnPropertyChanged(nameof(ProfileLoadVibrationState));
        OnPropertyChanged(nameof(IsProfileLoadVibrationOverridden));
        OnPropertyChanged(nameof(PowerOffIdleSelection));
        OnPropertyChanged(nameof(InputPollIntervalSelection));
        OnPropertyChanged(nameof(MotionPollIntervalSelection));
        OnPropertyChanged(nameof(BatteryAlertLevelSelection));
        OnPropertyChanged(nameof(BatteryAlertEffectSelection));
        OnPropertyChanged(nameof(ChargingLedEffectSelection));
        OnPropertyChanged(nameof(LedColorIntensityState));
        OnPropertyChanged(nameof(LedInheritsGlobal));
        OnPropertyChanged(nameof(BatteryAlertColorInherits));
    }

    public int PowerOffIdleSeconds
    {
        get => ControllerPowerOff.Normalize(Effective.PowerOffIdleSeconds);
        set
        {
            int normalized = ControllerPowerOff.Normalize(value);
            if (normalized == ControllerPowerOff.Normalize(Effective.PowerOffIdleSeconds))
            {
                return;
            }

            _ = _savePowerOffSettings(this, normalized);
        }
    }

    public void SetPowerOffSupport(string text)
    {
        if (_powerOffSupportTextBacking == text)
        {
            return;
        }

        _powerOffSupportTextBacking = text;
        PowerOffSupportText = text;
        OnPropertyChanged(nameof(PowerOffSupportText));
    }

    private string _powerOffSupportTextBacking =
        "Conecta el mando físico para consultar si se puede apagar.";

    public void SetBatteryInfo(string text)
    {
        if (BatteryInfoText == text)
        {
            return;
        }

        BatteryInfoText = text;
        OnPropertyChanged(nameof(BatteryInfoText));
    }

    private void NotifyBatteryProperties()
    {
        OnPropertyChanged(nameof(BatteryAlertEnabled));
        OnPropertyChanged(nameof(BatteryAlertLevel));
        OnPropertyChanged(nameof(BatteryAlertColorEnabled));
        OnPropertyChanged(nameof(BatteryAlertColorHex));
        OnPropertyChanged(nameof(BatteryAlertColorPreview));
        OnPropertyChanged(nameof(BatteryAlertEffectEnabled));
        OnPropertyChanged(nameof(BatteryAlertEffect));
        OnPropertyChanged(nameof(BatteryNotifyEnabled));
        OnPropertyChanged(nameof(BatterySoundEnabled));
        OnPropertyChanged(nameof(IgnoreExternalColorSignals));
        OnPropertyChanged(nameof(RestartPhysicalDeviceOnVirtualStart));
        BatteryAlertColorPreview = LedColorSlot.BrushFromHex(BatteryAlertColorHex);
    }

    private Task PickBatteryColorAsync()
    {
        string? picked = PickColor(BatteryAlertColorHex);
        if (picked is not null)
        {
            SetInheritableValue("batteryColorHex", picked);
        }

        return Task.CompletedTask;
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

    private sealed class KeyedCommand(Func<string?, Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await execute(parameter as string);
    }
}
