using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using GDM.Models;
using Brush = System.Windows.Media.Brush;

namespace GDM.ViewModels;

public sealed class GlobalPresetViewModel : INotifyPropertyChanged
{
    private readonly Func<GlobalPreset, Task> _save;
    private GlobalPreset _draft = GlobalPreset.Default;
    private readonly List<LedColorSlot> _ledPlayerColorSlots = [];
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _pickLedManualColorCommand;
    private readonly AsyncCommand _pickBatteryColorCommand;
    private bool _isDirty;

    public GlobalPresetViewModel(GlobalPreset preset, Func<GlobalPreset, Task> save)
    {
        _save = save;
        _saveCommand = new AsyncCommand(SaveAsync, () => _isDirty);
        _pickLedManualColorCommand = new AsyncCommand(
            () => PickManualColorAsync(),
            () => true);
        _pickBatteryColorCommand = new AsyncCommand(
            () => PickBatteryColorAsync(),
            () => true);
        LoadFrom(preset);
        for (int player = ControllerLedSettings.MinPlayerNumber;
             player <= ControllerLedSettings.MaxPlayerNumber;
             player++)
        {
            int playerNumber = player;
            _ledPlayerColorSlots.Add(new LedColorSlot(
                playerNumber,
                Led.PlayerColors.GetValueOrDefault(
                    playerNumber,
                    ControllerLedSettings.DefaultPlayerColors()[playerNumber]),
                PickPlayerColorAsync));
        }

        RefreshLedProperties();
    }

    public string DisplayName => ControllerProfile.GlobalPresetName;
    public string ConnectionStatus => "Ajuste global";
    public bool IsGlobalPresetEditor => true;
    public bool IsDirty => _isDirty;
    public ICommand SaveCommand => _saveCommand;
    public ICommand PickLedManualColorCommand => _pickLedManualColorCommand;
    public ICommand PickBatteryColorCommand => _pickBatteryColorCommand;

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
        new(0, "Desactivado"),
        new(60, "1 minuto"),
        new(300, "5 minutos"),
        new(900, "15 minutos"),
        new(1800, "30 minutos"),
        new(3600, "1 hora"),
        new(7200, "2 horas")
    ];
    public IReadOnlyList<SettingsViewModel.PollIntervalOption> InputPollIntervalOptions { get; } =
    [
        new(0, "Predeterminado"),
        new(4, "4 ms - respuesta máxima"),
        new(8, "8 ms - alta respuesta"),
        new(16, "16 ms - equilibrado"),
        new(33, "33 ms - bajo consumo"),
        new(50, "50 ms - mínimo consumo")
    ];
    public IReadOnlyList<SettingsViewModel.PollIntervalOption> MotionPollIntervalOptions { get; } =
    [
        new(0, "Predeterminado"),
        new(4, "4 ms"),
        new(8, "8 ms"),
        new(16, "16 ms"),
        new(33, "33 ms"),
        new(50, "50 ms")
    ];
    public IReadOnlyList<BatteryAlertLevelOption> BatteryAlertLevelOptions { get; } =
    [
        new BatteryAlertLevelOption((int)GamepadBatteryLevel.Empty, "Vacío o menos"),
        new BatteryAlertLevelOption((int)GamepadBatteryLevel.Low, "Baja o menos"),
        new BatteryAlertLevelOption((int)GamepadBatteryLevel.Medium, "Media o menos")
    ];
    public IReadOnlyList<LedModeOption> BatteryAlertEffectOptions { get; } =
    [
        new LedModeOption(BatteryAlertEffects.Blink, "Parpadeo (pulsos)"),
        new LedModeOption(BatteryAlertEffects.Fade, "Desvanecido")
    ];
    public IReadOnlyList<LedModeOption> ChargingLedEffectOptions { get; } =
    [
        new LedModeOption(ChargingLedEffects.None, "Ninguno"),
        new LedModeOption(ChargingLedEffects.Blink, "Parpadeo (pulsos)"),
        new LedModeOption(ChargingLedEffects.Fade, "Desvanecido")
    ];
    public IReadOnlyList<UnsavedDevicesOption> UnsavedDevicesOptions { get; } =
    [
        new UnsavedDevicesOption((int)GlobalPresetUnsavedMode.None, "No aplicar nada"),
        new UnsavedDevicesOption((int)GlobalPresetUnsavedMode.ViewOnly, "Solo aplicar ajustes"),
        new UnsavedDevicesOption((int)GlobalPresetUnsavedMode.TempTab, "Ajuste temporal con pestaña")
    ];
    public IReadOnlyList<LedColorSlot> LedPlayerColorSlots => _ledPlayerColorSlots;

    private ControllerSettings Settings => _draft.Settings;
    private ControllerLedSettings Led =>
        ControllerLedSettings.Normalize(Settings.Led);

    public bool VibrationEnabled
    {
        get => Settings.VibrationEnabled;
        set => MutateSettings(s => s with { VibrationEnabled = value });
    }

    public bool AutoStartVirtualGamepad
    {
        get => Settings.AutoStartVirtualGamepad;
        set => MutateSettings(s => s with { AutoStartVirtualGamepad = value });
    }

    public bool AutoHidePhysicalGamepad
    {
        get => Settings.AutoHidePhysicalGamepad;
        set => MutateSettings(s => s with { AutoHidePhysicalGamepad = value });
    }

    public bool KeepHiddenOnExit
    {
        get => Settings.KeepHiddenOnExit;
        set => MutateSettings(s => s with { KeepHiddenOnExit = value });
    }

    public bool RestartPhysicalDeviceOnVirtualStart
    {
        get => Settings.RestartPhysicalDeviceOnVirtualStart;
        set => MutateSettings(s => s with { RestartPhysicalDeviceOnVirtualStart = value });
    }

    public string LedMode
    {
        get => Led.Mode;
        set => SetLed(l => l with { Mode = ControllerLedModes.Normalize(value) });
    }

    public int LedBrightness
    {
        get => Led.Brightness;
        set => SetLed(l => l with
        {
            Brightness = Math.Clamp(value, ControllerLedSettings.MinBrightness, ControllerLedSettings.MaxBrightness)
        });
    }

    public int LedPlayerNumber
    {
        get => Led.PlayerNumber;
        set => SetLed(l => l with
        {
            PlayerNumber = Math.Clamp(value, ControllerLedSettings.MinPlayerNumber, ControllerLedSettings.MaxPlayerNumber)
        });
    }

    public string LedManualColorHex => Led.ManualColor ?? "FFFFFF";
    public Brush LedManualColorPreview { get; private set; } =
        LedColorSlot.BrushFromHex("FFFFFF");

    public int PowerOffIdleSeconds
    {
        get => ControllerPowerOff.Normalize(Settings.PowerOffIdleSeconds);
        set => MutateSettings(s => s with { PowerOffIdleSeconds = ControllerPowerOff.Normalize(value) });
    }

    public int InputPollIntervalMs
    {
        get => Settings.InputPollIntervalMs;
        set => MutateSettings(s => s with
        {
            InputPollIntervalMs = value <= 0 ? 0 : Math.Clamp(value, 4, 100)
        });
    }

    public bool MotionEnabled
    {
        get => Settings.MotionEnabled;
        set => MutateSettings(s => s with { MotionEnabled = value });
    }

    public int MotionPollIntervalMs
    {
        get => Settings.MotionPollIntervalMs;
        set => MutateSettings(s => s with
        {
            MotionPollIntervalMs = value <= 0 ? 0 : Math.Clamp(value, 4, 100)
        });
    }

    public bool BatteryAlertEnabled
    {
        get => Settings.BatteryAlertEnabled;
        set => MutateSettings(s => s with { BatteryAlertEnabled = value });
    }

    public int BatteryAlertLevel
    {
        get => BatteryAlertLevels.Normalize(Settings.BatteryAlertLevel);
        set => MutateSettings(s => s with { BatteryAlertLevel = BatteryAlertLevels.Normalize(value) });
    }

    public bool BatteryAlertColorEnabled
    {
        get => Settings.BatteryAlertColorEnabled;
        set => MutateSettings(s => s with { BatteryAlertColorEnabled = value });
    }

    public string BatteryAlertColorHex =>
        ControllerLedColor.NormalizeHex(Settings.BatteryAlertColor) ?? "FF453A";
    public Brush BatteryAlertColorPreview { get; private set; } =
        LedColorSlot.BrushFromHex("FF453A");

    public bool BatteryAlertEffectEnabled
    {
        get => Settings.BatteryAlertEffectEnabled;
        set => MutateSettings(s => s with { BatteryAlertEffectEnabled = value });
    }

    public string BatteryAlertEffect
    {
        get => BatteryAlertEffects.Normalize(Settings.BatteryAlertEffect);
        set => MutateSettings(s => s with { BatteryAlertEffect = BatteryAlertEffects.Normalize(value) });
    }

    public string ChargingLedEffect
    {
        get => ChargingLedEffects.Normalize(Settings.ChargingLedEffect);
        set => MutateSettings(s => s with { ChargingLedEffect = ChargingLedEffects.Normalize(value) });
    }

    public bool LedEffectColorIntensity
    {
        get => Settings.LedEffectColorIntensity;
        set => MutateSettings(s => s with { LedEffectColorIntensity = value });
    }

    public bool BatteryNotifyEnabled
    {
        get => Settings.BatteryNotifyEnabled;
        set => MutateSettings(s => s with { BatteryNotifyEnabled = value });
    }

    public bool BatterySoundEnabled
    {
        get => Settings.BatterySoundEnabled;
        set => MutateSettings(s => s with { BatterySoundEnabled = value });
    }

    public bool IgnoreExternalColorSignals
    {
        get => Settings.IgnoreExternalColorSignals;
        set => MutateSettings(s => s with { IgnoreExternalColorSignals = value });
    }

    public int UnsavedDevicesMode
    {
        get => (int)_draft.UnsavedDevices;
        set => Mutate(preset => preset with
        {
            UnsavedDevices = GlobalPresetModes.Normalize(value)
        });
    }

    public void LoadFrom(GlobalPreset preset)
    {
        _draft = preset;
        _isDirty = false;
        _saveCommand.NotifyCanExecuteChanged();
        RefreshLedProperties();
        NotifyAll();
    }

    private void MutateSettings(Func<ControllerSettings, ControllerSettings> mutate) =>
        Mutate(preset => preset with { Settings = mutate(preset.Settings) });

    private void SetLed(Func<ControllerLedSettings, ControllerLedSettings> mutate) =>
        MutateSettings(s => s with { Led = mutate(ControllerLedSettings.Normalize(s.Led)) });

    private void Mutate(Func<GlobalPreset, GlobalPreset> mutate)
    {
        _draft = mutate(_draft);
        _isDirty = true;
        _saveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }

    private async Task SaveAsync()
    {
        ControllerSettings normalized = Settings with
        {
            Led = ControllerLedSettings.Normalize(Settings.Led),
            BatteryAlertLevel = BatteryAlertLevels.Normalize(Settings.BatteryAlertLevel),
            BatteryAlertColor = ControllerLedColor.NormalizeHex(Settings.BatteryAlertColor) ?? "FF453A",
            BatteryAlertEffect = BatteryAlertEffects.Normalize(Settings.BatteryAlertEffect),
            ChargingLedEffect = ChargingLedEffects.Normalize(Settings.ChargingLedEffect),
            PowerOffIdleSeconds = ControllerPowerOff.Normalize(Settings.PowerOffIdleSeconds),
            InputPollIntervalMs = Settings.InputPollIntervalMs <= 0
                ? 0
                : Math.Clamp(Settings.InputPollIntervalMs, 4, 100),
            MotionPollIntervalMs = Settings.MotionPollIntervalMs <= 0
                ? 0
                : Math.Clamp(Settings.MotionPollIntervalMs, 4, 100)
        };
        GlobalPreset preset = _draft with
        {
            Settings = normalized,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await _save(preset);
        _isDirty = false;
        _saveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDirty));
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

        BatteryAlertColorPreview = LedColorSlot.BrushFromHex(BatteryAlertColorHex);
        OnPropertyChanged(nameof(LedManualColorHex));
        OnPropertyChanged(nameof(LedManualColorPreview));
        OnPropertyChanged(nameof(BatteryAlertColorHex));
        OnPropertyChanged(nameof(BatteryAlertColorPreview));
    }

    private Task PickManualColorAsync()
    {
        string? picked = PickColor(LedManualColorHex);
        if (picked is not null)
        {
            SetLed(l => l with { ManualColor = picked });
            RefreshLedProperties();
        }

        return Task.CompletedTask;
    }

    private Task PickPlayerColorAsync(LedColorSlot slot)
    {
        string? picked = PickColor(slot.ColorHex);
        if (picked is not null)
        {
            var colors = new Dictionary<int, string>(Led.PlayerColors)
            {
                [slot.PlayerNumber] = picked
            };
            SetLed(l => l with { PlayerColors = colors });
            RefreshLedProperties();
        }

        return Task.CompletedTask;
    }

    private Task PickBatteryColorAsync()
    {
        string? picked = PickColor(BatteryAlertColorHex);
        if (picked is not null)
        {
            MutateSettings(s => s with { BatteryAlertColor = picked });
            RefreshLedProperties();
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

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(VibrationEnabled));
        OnPropertyChanged(nameof(AutoStartVirtualGamepad));
        OnPropertyChanged(nameof(AutoHidePhysicalGamepad));
        OnPropertyChanged(nameof(KeepHiddenOnExit));
        OnPropertyChanged(nameof(RestartPhysicalDeviceOnVirtualStart));
        OnPropertyChanged(nameof(LedMode));
        OnPropertyChanged(nameof(LedBrightness));
        OnPropertyChanged(nameof(LedPlayerNumber));
        OnPropertyChanged(nameof(PowerOffIdleSeconds));
        OnPropertyChanged(nameof(InputPollIntervalMs));
        OnPropertyChanged(nameof(MotionEnabled));
        OnPropertyChanged(nameof(MotionPollIntervalMs));
        OnPropertyChanged(nameof(BatteryAlertEnabled));
        OnPropertyChanged(nameof(BatteryAlertLevel));
        OnPropertyChanged(nameof(BatteryAlertColorEnabled));
        OnPropertyChanged(nameof(BatteryAlertEffectEnabled));
        OnPropertyChanged(nameof(BatteryAlertEffect));
        OnPropertyChanged(nameof(ChargingLedEffect));
        OnPropertyChanged(nameof(LedEffectColorIntensity));
        OnPropertyChanged(nameof(BatteryNotifyEnabled));
        OnPropertyChanged(nameof(BatterySoundEnabled));
        OnPropertyChanged(nameof(IgnoreExternalColorSignals));
        OnPropertyChanged(nameof(UnsavedDevicesMode));
        OnPropertyChanged(nameof(IsDirty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed record UnsavedDevicesOption(int Value, string Label);

    private sealed class AsyncCommand(
        Func<Task> execute,
        Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();
        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
