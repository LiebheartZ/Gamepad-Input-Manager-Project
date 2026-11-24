using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GDM.Models;
using GDM.Services;
using GDM.Services.Abstractions;

namespace GDM.ViewModels;

public sealed class ProfilesViewModel : INotifyPropertyChanged
{
    private readonly IProfileRepository? _profileRepository;
    private readonly IControllerRepository? _controllerRepository;
    private readonly Action<ControllerRecord> _onControllerUpdated;
    private readonly Func<ControllerProfile, bool>? _confirmDelete;
    private readonly Action<string, Exception?>? _reportError;
    private readonly Action<DiagnosticLogLevel, string, Exception?>? _writeDiagnostic;
    private readonly IGamepadInputService? _inputService;
    private readonly IVirtualGamepadService? _virtualGamepadService;
    private readonly Func<ConnectedGamepad, bool>? _requestElevatedVirtualGamepad;
    private readonly Func<ConnectedGamepad, Task<bool>>? _hidePhysicalDevice;
    private readonly Func<ConnectedGamepad, Task<bool>>? _showPhysicalDevice;
    private readonly Func<ConnectedGamepad, bool>? _isPhysicalDevicePresent;
    private readonly Func<ConnectedGamepad, bool>? _isDeviceHiddenByGdm;
    private readonly Func<string>? _defaultVirtualGamepadType;
    private readonly Func<int>? _defaultInputPollIntervalMs;
    private readonly Dictionary<string, long> _lastVirtualPollReadMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastLedIdleRefreshMs = new(StringComparer.Ordinal);
    private readonly ISonyControllerLedService? _ledService;
    private readonly IControllerPowerOffService? _powerOffService;
    private readonly Dictionary<string, SonyLedApplyResult> _lastLedResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (byte Red, byte Green, byte Blue, byte Brightness, int? Player)> _lastLedWrites =
        new(StringComparer.Ordinal);
    private long _lastRumbleForwardLogMs;
    private readonly Dictionary<string, RumbleHoldState> _rumbleHoldStates = new(StringComparer.Ordinal);
    private System.Windows.Threading.DispatcherTimer? _rumbleHoldTimer;
    private readonly Dictionary<string, long> _virtualDeviceMissingSinceMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _physicalPresenceCheckMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _physicalPresenceMissingSinceMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPhysicalRescanMs = new(StringComparer.Ordinal);
    private const long VirtualDeviceMissingGraceMs = 3000;
    private const long PhysicalPresenceCheckIntervalMs = 2000;
    private const long PhysicalPresenceMissingGraceMs = 4000;
    private const long PhysicalPresenceRescanIntervalMs = 10000;
    private const long PhysicalPresenceDefinitiveMissingMs = 15000;
    private readonly HashSet<string> _phantomDeviceKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _batteryAlertActive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GamepadBatteryLevel> _lastBatteryLevels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastBatteryReadMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _batteryAlertNotifyMs = new(StringComparer.Ordinal);
    private System.Windows.Threading.DispatcherTimer? _batteryEffectTimer;
    private System.Windows.Threading.DispatcherTimer? _ledGuardTimer;
    private const long BatteryReadIntervalMs = 1000;
    private const long BatteryNotifyRepeatMs = 60000;
    private sealed record RumbleHoldState(int Low, int High, long LastEventMs);
    private readonly Dictionary<Guid, string> _powerOffSupportCheckedKeys = new();
    private readonly Dictionary<Guid, IdlePowerOffState> _idlePowerOffStates = new();
    private readonly AsyncCommand _createCommand;
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _duplicateCommand;
    private readonly AsyncCommand _deleteCommand;
    private readonly AsyncCommand _restoreCommand;
    private readonly AsyncCommand _assignCommand;
    private readonly GamepadComboEvaluator _comboEvaluator = new();
    private readonly GamepadComboEvaluator _saveComboEvaluator = new();
    private readonly Dictionary<string, long> _saveComboLastReadMs = new(StringComparer.Ordinal);
    private const long SaveComboPollIntervalMs = 50;
    private static readonly Guid SaveNewControllerComboId =
        new("6b1d4f2e-8c3a-4e5b-9d7f-2a1c8e4b6d3f");
    private ControllerListItem[] _connectedControllerItems = [];
    private ConnectedGamepad[] _allConnectedDevices = [];
    private readonly Dictionary<Guid, string> _autoStartAttempts = [];
    private readonly HashSet<Guid> _autoStartSuppressed = [];
    private readonly HashSet<Guid> _autoHideSuppressedControllers = [];
    private readonly List<Task> _startupAutoStartTasks = [];
    private readonly HashSet<string> _autoHiddenDeviceKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevicePresenceState> _autoHiddenPresenceStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevicePresenceState> _hiddenInputUnavailableStates = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _lastRebindDiagnostics = [];
    private readonly Dictionary<string, int> _lastComboProfileLog = new(StringComparer.Ordinal);
    private ControllerProfile? _selectedProfile;
    private ControllerProfile? _selectedProfileTab;
    private readonly HashSet<Guid> _legacyBuiltinProfileIds = [];
    private ControllerRecord? _selectedController;
    private object? _selectedVirtualGamepad;
    private ProfileChoice? _selectedAssignmentProfile;
    private ControllerRecord? _editingDeviceRecord;
    private string _draftName = string.Empty;
    private string _draftDescription = string.Empty;
    private string _draftVirtualGamepadType = VirtualGamepadTypes.Default;
    private readonly Dictionary<string, ControllerProfileMapping> _typeMappingDrafts =
        new(StringComparer.Ordinal);
    private bool _isLoadingProfileDraft;
    private double _leftStickDeadZone;
    private double _rightStickDeadZone;
    private string _statusMessage = "Los perfiles aún no se han cargado.";
    private bool _isBusy;
    private bool _isInitialized;
    private bool _controllersLoaded;
    private bool _elevationRequested;
    private Task? _initializationTask;
    private readonly IGlobalPresetRepository? _globalPresetRepository;
    private GlobalPreset _globalPreset = GlobalPreset.Default;
    private readonly Func<ConnectedGamepad, Task<bool>>? _saveUnsavedController;
    private readonly Action<string, string[]>? _notifyEvent;

    private sealed record EffectiveSettingsCacheEntry(
        GlobalPreset Preset,
        ControllerSettings Settings);

    private readonly ConditionalWeakTable<ControllerRecord, EffectiveSettingsCacheEntry>
        _effectiveSettingsCache = new();
    private readonly List<ControllerRecord> _temporaryRecords = [];
    private readonly HashSet<Guid> _temporaryControllerIds = [];
    private readonly Dictionary<Guid, string> _temporaryDeviceKeys = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? VirtualPollIntervalChanged;

    public ObservableCollection<ControllerProfile> Profiles { get; } = [];
    public ObservableCollection<ControllerRecord> SavedControllers { get; } = [];
    public ObservableCollection<ProfileChoice> AssignmentChoices { get; } = [];
    public ObservableCollection<ProfileMappingRow> ButtonMappingRows { get; } = [];
    public ObservableCollection<ProfileMappingRow> AxisMappingRows { get; } = [];
    public ObservableCollection<ProfileComboRow> ComboRows { get; } = [];
    public ObservableCollection<ControllerProfile> ProfileEditorTabs { get; } = [];
    public IReadOnlyList<VirtualGamepadTypeOption> VirtualGamepadTypeOptions { get; } =
        VirtualGamepadTypes.Options;

    public ObservableCollection<VirtualGamepadControllerViewModel> VirtualGamepads { get; } = [];
    public ICommand CreateCommand => _createCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand DuplicateCommand => _duplicateCommand;
    public ICommand DeleteCommand => _deleteCommand;
    public ICommand RestoreCommand => _restoreCommand;
    public ICommand AssignCommand => _assignCommand;
    public ICommand AddComboCommand { get; }
    public ICommand RemoveComboCommand { get; }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandsCanExecuteChanged();
            }
        }
    }

    public bool IsInitialized => _isInitialized;
    public bool IsAnyVirtualGamepadRunning => _virtualGamepadService?.IsRunning == true;
    public bool IsAnyVirtualGamepadBusy => VirtualGamepads.Any(item => item.IsStarting);
    public bool HasAutoStartVirtualGamepad =>
        SavedControllers.Concat(_temporaryRecords)
            .Any(controller => EffectiveSettings(controller).AutoStartVirtualGamepad);

    public GlobalPreset GlobalPreset => _globalPreset;
    public GlobalPresetViewModel? GlobalPresetEditor { get; private set; }
    public ObservableCollection<object> PerfilesTabs { get; } = [];

    public string DraftName
    {
        get => _draftName;
        set
        {
            if (SetProperty(ref _draftName, value))
            {
                _saveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DraftDescription
    {
        get => _draftDescription;
        set => SetProperty(ref _draftDescription, value);
    }

    public string DraftVirtualGamepadType
    {
        get => _draftVirtualGamepadType;
        set
        {
            if (EqualityComparer<string>.Default.Equals(_draftVirtualGamepadType, value))
            {
                return;
            }

            string previousType = _draftVirtualGamepadType;
            _draftVirtualGamepadType = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(DraftVirtualGamepadType)));
            if (!_isLoadingProfileDraft)
            {
                CaptureMappingDraft(previousType);
                LoadControlMappingRows(GetMappingDraft(SelectedProfile, value));
                RefreshComboTargetButtons();
            }

            OnPropertyChanged(nameof(DraftVirtualGamepadTypeName));
        }
    }

    public string DraftVirtualGamepadTypeName =>
        VirtualGamepadTypes.IsAuto(_draftVirtualGamepadType)
            ? $"Automático ({VirtualGamepadTypes.DisplayName(ResolveEditorVirtualGamepadType(_draftVirtualGamepadType))})"
            : VirtualGamepadTypes.DisplayName(_draftVirtualGamepadType);

    public double LeftStickDeadZone
    {
        get => _leftStickDeadZone;
        set => SetProperty(ref _leftStickDeadZone, Math.Clamp(value, 0d, 0.5d));
    }

    public double RightStickDeadZone
    {
        get => _rightStickDeadZone;
        set => SetProperty(ref _rightStickDeadZone, Math.Clamp(value, 0d, 0.5d));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ControllerProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            _isLoadingProfileDraft = true;
            try
            {
                DraftName = value?.DisplayName ?? string.Empty;
                DraftDescription = value?.Description ?? string.Empty;
                DraftVirtualGamepadType = _editingDeviceRecord is { } editingRecord
                    ? editingRecord.DeviceProfile?.VirtualGamepadType is { } deviceType &&
                      (VirtualGamepadTypes.IsSupported(deviceType) ||
                       VirtualGamepadTypes.IsAuto(deviceType))
                        ? deviceType
                        : VirtualGamepadTypes.InferFromController(editingRecord) ??
                            VirtualGamepadTypes.Xbox360
                    : value?.VirtualGamepadType is { } type &&
                      (VirtualGamepadTypes.IsSupported(type) ||
                       VirtualGamepadTypes.IsAuto(type))
                        ? type
                        : ResolveDefaultVirtualGamepadType();
            }
            finally
            {
                _isLoadingProfileDraft = false;
            }

            _typeMappingDrafts.Clear();
            LoadMappingRows(value);
            if (value is not null && ProfileEditorTabs.Contains(value))
            {
                _selectedProfileTab = value;
                OnPropertyChanged(nameof(SelectedProfileTab));
            }

            OnPropertyChanged(nameof(ShowDeleteButton));
            OnPropertyChanged(nameof(ShowRestoreButton));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public ControllerProfile? SelectedProfileTab
    {
        get => _selectedProfileTab;
        set
        {
            if (value is not null)
            {
                _selectedProfileTab = value;
                OnPropertyChanged();
                SelectedProfile = value;
            }
        }
    }

    public ControllerRecord? SelectedController
    {
        get => _selectedController;
        set
        {
            Guid? previousId = _selectedController?.ControllerId;
            if (!SetProperty(ref _selectedController, value))
            {
                return;
            }

            bool sameController = previousId is not null &&
                value?.ControllerId == previousId;
            if (!sameController ||
                (_selectedAssignmentProfile is not null &&
                 !AssignmentChoices.Contains(_selectedAssignmentProfile)))
            {
                UpdateSelectedAssignmentChoice();
            }

            bool wasEditingDevice = _editingDeviceRecord?.ControllerId ==
                value?.ControllerId;
            _editingDeviceRecord = value is not null &&
                _selectedAssignmentProfile is { ProfileId: null }
                    ? value
                    : null;
            if (_editingDeviceRecord is not null && !wasEditingDevice)
            {
                SelectedProfile = DeviceConfigBaseline(_editingDeviceRecord);
            }

            OnPropertyChanged(nameof(IsEditingDeviceProfile));
            OnPropertyChanged(nameof(ShowDeleteButton));
            OnPropertyChanged(nameof(ShowRestoreButton));
            OnPropertyChanged(nameof(IsNotEditingDeviceProfile));
            OnPropertyChanged(nameof(EditorTitle));
            NotifyCommandsCanExecuteChanged();
            _assignCommand.NotifyCanExecuteChanged();
        }
    }

    public ProfileChoice? SelectedAssignmentProfile
    {
        get => _selectedAssignmentProfile;
        set
        {
            if (SetProperty(ref _selectedAssignmentProfile, value))
            {
                if (value?.ProfileId is Guid profileId)
                {
                    _editingDeviceRecord = null;
                    ControllerProfile? match = Profiles.FirstOrDefault(
                        profile => profile.ProfileId == profileId);
                    if (match is not null)
                    {
                        SelectedProfile = match;
                    }
                }
                else if (value is not null && SelectedController is not null)
                {
                    _editingDeviceRecord = SelectedController;
                    SelectedProfile = DeviceConfigBaseline(SelectedController);
                }

                OnPropertyChanged(nameof(IsEditingDeviceProfile));
                OnPropertyChanged(nameof(ShowDeleteButton));
                OnPropertyChanged(nameof(ShowRestoreButton));
                OnPropertyChanged(nameof(IsNotEditingDeviceProfile));
                OnPropertyChanged(nameof(EditorTitle));
                NotifyCommandsCanExecuteChanged();
                _assignCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public object? SelectedVirtualGamepad
    {
        get => _selectedVirtualGamepad;
        set
        {
            if (SetProperty(ref _selectedVirtualGamepad, value))
            {
                SelectedController = value is VirtualGamepadControllerViewModel item
                    ? item.Record
                    : null;
                if (value is GlobalPresetViewModel)
                {
                    ControllerProfile? global = Profiles.FirstOrDefault(
                        ControllerProfile.IsGlobalPreset);
                    if (global is not null && !ReferenceEquals(SelectedProfile, global))
                    {
                        SelectedProfile = global;
                    }
                }
            }
        }
    }

    public bool IsEditingDeviceProfile => _editingDeviceRecord is not null;
    public bool IsNotEditingDeviceProfile => _editingDeviceRecord is null;
    public string EditorTitle => IsEditingDeviceProfile
        ? "Perfil por mando"
        : "Detalles del perfil";

    public bool ShowDeleteButton => !IsEditingDeviceProfile &&
        SelectedProfile is not null && !IsBuiltinProfile(SelectedProfile);
    public bool ShowRestoreButton =>
        !IsEditingDeviceProfile && SelectedProfile is not null;

    public bool HasVirtualGamepads => VirtualGamepads.Count > 0;    public bool ShowNoVirtualGamepads => VirtualGamepads.Count == 0;

    public ProfilesViewModel(
        IProfileRepository? profileRepository,
        IControllerRepository? controllerRepository,
        Action<ControllerRecord>? onControllerUpdated = null,
        Func<ControllerProfile, bool>? confirmDelete = null,
        Action<string, Exception?>? reportError = null,
        IGamepadInputService? inputService = null,
        IVirtualGamepadService? virtualGamepadService = null,
        Func<ConnectedGamepad, bool>? requestElevatedVirtualGamepad = null,
        Func<ConnectedGamepad, Task<bool>>? hidePhysicalDevice = null,
        Func<ConnectedGamepad, Task<bool>>? showPhysicalDevice = null,
        Func<ConnectedGamepad, bool>? isPhysicalDevicePresent = null,
        Action<DiagnosticLogLevel, string, Exception?>? writeDiagnostic = null,
        Func<string>? defaultVirtualGamepadType = null,
        ISonyControllerLedService? ledService = null,
        IControllerPowerOffService? powerOffService = null,
        Func<int>? defaultInputPollIntervalMs = null,
        Func<ConnectedGamepad, bool>? isDeviceHiddenByGdm = null,
        IGlobalPresetRepository? globalPresetRepository = null,
        Func<ConnectedGamepad, Task<bool>>? saveUnsavedController = null,
        Action<string, string[]>? notifyEvent = null)
    {
        _profileRepository = profileRepository;
        _controllerRepository = controllerRepository;
        _onControllerUpdated = onControllerUpdated ?? (_ => { });
        _confirmDelete = confirmDelete;
        _reportError = reportError;
        _writeDiagnostic = writeDiagnostic;
        _inputService = inputService;
        _virtualGamepadService = virtualGamepadService;
        _requestElevatedVirtualGamepad = requestElevatedVirtualGamepad;
        _hidePhysicalDevice = hidePhysicalDevice;
        _showPhysicalDevice = showPhysicalDevice;
        _isPhysicalDevicePresent = isPhysicalDevicePresent;
        _isDeviceHiddenByGdm = isDeviceHiddenByGdm;
        _globalPresetRepository = globalPresetRepository;
        _saveUnsavedController = saveUnsavedController;
        _notifyEvent = notifyEvent;
        _defaultVirtualGamepadType = defaultVirtualGamepadType;
        _ledService = ledService;
        _powerOffService = powerOffService;
        _defaultInputPollIntervalMs = defaultInputPollIntervalMs;

        if (_virtualGamepadService is not null)
        {
            _virtualGamepadService.RumbleReceived += OnVirtualRumbleOutput;
        }
        _createCommand = new AsyncCommand(CreateAsync, () =>
            CanEditProfiles && !IsEditingDeviceProfile, ReportCommandFailure);
        _saveCommand = new AsyncCommand(SaveAsync, () =>
            CanEditProfiles &&
            (IsEditingDeviceProfile || (SelectedProfile is not null &&
             !string.IsNullOrWhiteSpace(DraftName) && DraftName.Trim().Length <= 80)),
            ReportCommandFailure);
        _duplicateCommand = new AsyncCommand(DuplicateAsync, () =>
            CanEditProfiles && SelectedProfile is not null && !IsEditingDeviceProfile,
            ReportCommandFailure);
        _deleteCommand = new AsyncCommand(DeleteAsync, () =>
            CanEditProfiles && (SelectedProfile is not null || IsEditingDeviceProfile),
            ReportCommandFailure);
        _restoreCommand = new AsyncCommand(RestoreAsync, () =>
            CanEditProfiles && (SelectedProfile is not null || IsEditingDeviceProfile),
            ReportCommandFailure);
        _assignCommand = new AsyncCommand(AssignAsync, () =>
            !IsBusy && _controllerRepository is not null &&
            SelectedController is not null && SelectedAssignmentProfile is not null,
            ReportCommandFailure);
        AddComboCommand = new ParameterCommand(_ =>
        {
            ComboRows.Add(new ProfileComboRow(
                Guid.NewGuid(),
                string.Empty,
                [],
                requiresHold: false,
                holdMilliseconds: 0,
                ComboActionTypes.Buttons,
                []));
        });
        RemoveComboCommand = new ParameterCommand(parameter =>
        {
            if (parameter is ProfileComboRow row)
            {
                ComboRows.Remove(row);
            }
        });
        RebuildAssignmentChoices();
    }

    private bool CanEditProfiles => !IsBusy && _profileRepository is not null;

    public Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return Task.CompletedTask;
        }

        if (_initializationTask is null || _initializationTask.IsCompleted)
        {
            _initializationTask = LoadProfilesAsync();
        }

        return _initializationTask;
    }

    public Task DeleteSelectedProfileAsync() => DeleteAsync();

    public Task AssignSelectedProfileAsync() => AssignAsync();

    public Task DuplicateSelectedProfileAsync() => DuplicateAsync();

    public Task SaveSelectedProfileAsync() => SaveAsync();

    public Task CreateProfileAsync() => CreateAsync();

    public void UpdateSavedControllers(IEnumerable<ControllerRecord> controllers)
    {
        _controllersLoaded = true;
        bool hadAutoStart = HasAutoStartVirtualGamepad;
        Guid? selectedId = SelectedController?.ControllerId;
        SavedControllers.Clear();
        foreach (ControllerRecord controller in controllers)
        {
            SavedControllers.Add(controller);
        }

        SelectedController = selectedId is Guid id
            ? SavedControllers.SingleOrDefault(controller => controller.ControllerId == id)
            : SavedControllers.FirstOrDefault();
        RebuildAssignmentChoices();
        _ = RemapLegacyBuiltinAssignmentsAsync();
        if (hadAutoStart != HasAutoStartVirtualGamepad)
        {
            OnPropertyChanged(nameof(HasAutoStartVirtualGamepad));
        }

        SynchronizeVirtualGamepads();
    }

    public void UpdateConnectedControllers(IEnumerable<ControllerListItem> controllers)
    {
        _connectedControllerItems = controllers.ToArray();
        SynchronizeVirtualGamepads();
    }

    public void UpdateAllConnectedDevices(IEnumerable<ConnectedGamepad> devices)
    {
        _allConnectedDevices = devices.ToArray();
    }

    /// <summary>
    /// Elimina el estado interno asociado a claves SDL que ya no están
    /// presentes. Solo purga cachés de sondeo; nunca toca el seguimiento de
    /// dispositivos ocultos por HidHide, necesario para la reconciliación.
    /// </summary>
    public void PruneStaleDeviceState(IReadOnlyCollection<string> activeDeviceKeys)
    {
        var active = new HashSet<string>(activeDeviceKeys, StringComparer.Ordinal);
        bool IsStale(string key) => !active.Contains(key);

        RemoveWhere(_lastVirtualPollReadMs, IsStale);
        RemoveWhere(_lastBatteryReadMs, IsStale);
        RemoveWhere(_lastBatteryLevels, IsStale);
        RemoveWhere(_batteryAlertActive, IsStale);
        RemoveWhere(_batteryAlertNotifyMs, IsStale);
        RemoveWhere(_lastLedIdleRefreshMs, IsStale);
        RemoveWhere(_lastComboProfileLog, IsStale);
        RemoveWhere(_lastLedWrites, IsStale);
        RemoveWhere(_lastLedResults, IsStale);
        RemoveWhere(_virtualDeviceMissingSinceMs, IsStale);
        RemoveWhere(_physicalPresenceCheckMs, IsStale);
        RemoveWhere(_physicalPresenceMissingSinceMs, IsStale);
        RemoveWhere(_lastPhysicalRescanMs, IsStale);
        RemoveWhere(_saveComboLastReadMs, IsStale);
        _comboEvaluator.RemoveStale(active);
        _saveComboEvaluator.RemoveStale(active);
    }

    private static void RemoveWhere<TKey, TValue>(
        Dictionary<TKey, TValue> dictionary,
        Func<TKey, bool> predicate)
        where TKey : notnull
    {
        foreach (TKey key in dictionary.Keys.Where(predicate).ToArray())
        {
            dictionary.Remove(key);
        }
    }

    public async Task ReadVirtualGamepadInputsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_inputService is null || _virtualGamepadService is null)
        {
            return;
        }

        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads.ToArray())
        {
            if (item.ConnectedDevice is not { } device)
            {
                continue;
            }

            if (!_virtualGamepadService.IsRunningFor(device.DeviceKey))
            {
                if (item.IsRunning)
                {
                    await RestoreAutoHiddenDeviceAsync(device);
                    item.SetStatus("La salida virtual está detenida.");
                    item.Update(
                        item.Record,
                        device,
                        item.ProfileName,
                        isRunning: false,
                        ResolveVirtualGamepadType(item.Record));
                    NotifyVirtualOutputStateChanged();
                }

                await UpdateBatteryAlertAsync(item, device, cancellationToken);
                await PollCombosWhileOutputStoppedAsync(item, device, cancellationToken);
                long idleNowMs = Environment.TickCount64;
                if (!_lastLedIdleRefreshMs.TryGetValue(device.DeviceKey, out long lastLedRefresh) ||
                    idleNowMs - lastLedRefresh >= 2000)
                {
                    _lastLedIdleRefreshMs[device.DeviceKey] = idleNowMs;
                    _ = ApplyLedStateAsync(item);
                }
                continue;
            }

            int pollIntervalMs = EffectiveInputPollIntervalMs(item.Record);
            long pollNowMs = Environment.TickCount64;
            if (_lastVirtualPollReadMs.TryGetValue(device.DeviceKey, out long lastPollMs) &&
                pollNowMs - lastPollMs < pollIntervalMs)
            {
                continue;
            }

            _lastVirtualPollReadMs[device.DeviceKey] = pollNowMs;

            try
            {
                GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(
                    device.DeviceKey,
                    cancellationToken);
                if (snapshot is null)
                {
                    if (_autoHiddenDeviceKeys.Contains(device.DeviceKey))
                    {
                        if (await ConfirmPhysicalDisconnectAsync(device))
                        {
                            await _virtualGamepadService.StopAsync(device.DeviceKey, cancellationToken);
                            _comboEvaluator.Reset(device.DeviceKey);
                            await RestoreAutoHiddenDeviceAsync(device);
                            _autoHiddenPresenceStates.Remove(device.DeviceKey);
                            _hiddenInputUnavailableStates.Remove(device.DeviceKey);
                            item.SetStatus("La salida virtual se detuvo porque se desconectó el mando físico.");
                            item.Update(
                                item.Record,
                                null,
                                item.ProfileName,
                                isRunning: false,
                                ResolveVirtualGamepadType(item.Record));
                            NotifyVirtualOutputStateChanged();
                            continue;
                        }

                        if (await ConfirmHiddenInputUnavailableAsync(device))
                        {
                            // SDL no puede leer el mando mientras HidHide lo oculta
                            // (p. ej. mandos Sony por hidapi). Se pausa el
                            // auto-ocultamiento de este controlador durante la
                            // sesión para que el reinicio automático no vuelva a
                            // ocultarlo y entrar en bucle.
                            _autoHideSuppressedControllers.Add(item.Record.ControllerId);
                            bool physicalDeviceRestored =
                                await RestoreAutoHiddenDeviceAsync(device, force: true);
                            WriteAutoHideDiagnostic(
                                DiagnosticLogLevel.Warning,
                                $"Se agotó la espera de lectura. Controlador={item.Record.ControllerId}; " +
                                $"mando={DescribeDevice(device)}; restaurado={physicalDeviceRestored}; " +
                                $"salidaVirtualActiva={_virtualGamepadService.IsRunningFor(device.DeviceKey)}.");
                            item.SetStatus(physicalDeviceRestored
                                ? "SDL dejó de leer este mando al ocultarlo. Se volvió a mostrar y el ocultamiento automático se pausó durante esta sesión."
                                : "SDL dejó de leer este mando al ocultarlo y GDM no pudo volver a mostrarlo. Revisa HidHide; el ocultamiento automático se pausó durante esta sesión.");
                            _reportError?.Invoke(
                                item.StatusMessage,
                                new InvalidOperationException(
                                    physicalDeviceRestored
                                        ? "SDL no pudo leer el mando físico mientras HidHide lo mantenía oculto."
                                        : "SDL no pudo leer el mando físico y GDM no pudo restaurar su visibilidad."));
                            continue;
                        }

                        item.SetStatus(
                            "El mando físico está oculto; se mantiene activa la salida virtual.");
                        continue;
                    }

                    _hiddenInputUnavailableStates.Remove(device.DeviceKey);
                    await _virtualGamepadService.StopAsync(device.DeviceKey, cancellationToken);
                    _comboEvaluator.Reset(device.DeviceKey);
                    await RestoreAutoHiddenDeviceAsync(device);
                    item.SetStatus("La salida se detuvo porque se desconectó el mando físico.");
                    item.Update(
                        item.Record,
                        null,
                        item.ProfileName,
                        isRunning: false,
                        ResolveVirtualGamepadType(item.Record));
                    NotifyVirtualOutputStateChanged();
                    continue;
                }

                EvaluateBatteryAlert(item, device, snapshot.BatteryLevel);
                ControllerProfile? profile = FindAssignedProfile(item.Record);
                string? runningType = _virtualGamepadService
                    .GetRunningVirtualGamepadType(device.DeviceKey);
                string effectiveType = VirtualGamepadTypes.IsSupported(runningType)
                    ? runningType!
                    : ResolveVirtualGamepadType(item.Record);
                if (await CheckPhysicalPresenceAsync(item, device))
                {
                    await _virtualGamepadService.StopAsync(device.DeviceKey, cancellationToken);
                    _comboEvaluator.Reset(device.DeviceKey);
                    await RestoreAutoHiddenDeviceAsync(device);
                    item.SetStatus("La salida se detuvo porque Windows ya no detecta el mando físico.");
                    item.Update(
                        item.Record,
                        null,
                        item.ProfileName,
                        isRunning: false,
                        ResolveVirtualGamepadType(item.Record));
                    NotifyVirtualOutputStateChanged();
                    continue;
                }

                if (!item.IsStarting &&
                    await CheckVirtualDevicePresenceAsync(
                        item, device, snapshot, profile, runningType, cancellationToken))
                {
                    continue;
                }

                _hiddenInputUnavailableStates.Remove(device.DeviceKey);
                int comboProfileCount = profile?.Combos?.Count ?? -1;
                if (_lastComboProfileLog.GetValueOrDefault(device.DeviceKey) != comboProfileCount)
                {
                    _lastComboProfileLog[device.DeviceKey] = comboProfileCount;
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Information,
                        $"Perfil para combos. Controlador={item.Record.ControllerId}; " +
                        $"perfil=\"{profile?.DisplayName ?? "(ninguno)"}\"; combos={comboProfileCount}; " +
                        $"mando={DescribeDevice(device)}.");
                }

                ComboEvaluation comboEvaluation = _comboEvaluator.Evaluate(
                    device.DeviceKey,
                    snapshot,
                    profile?.Combos);
                GamepadInputSnapshot submitSnapshot = ApplyMotionBias(item.Record, snapshot);
                _virtualGamepadService.SubmitInputSnapshot(
                    device,
                    submitSnapshot,
                    profile?.WithMappingsFor(effectiveType),
                    GetComboTargetButtons(comboEvaluation),
                    EffectiveSettings(item.Record).MotionEnabled
                        ? EffectiveMotionPollIntervalMs(item.Record)
                        : (int?)null);
                await ApplyComboActionsAsync(item, device, snapshot, profile, comboEvaluation);
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException or
                ObjectDisposedException or
                InvalidOperationException or
                IOException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
            {
                if (_autoHiddenDeviceKeys.Contains(device.DeviceKey))
                {
                    item.SetStatus(
                        $"No se pudo leer el mando físico oculto; se mantiene activa la salida virtual. {exception.Message}");
                    _reportError?.Invoke(item.StatusMessage, exception);
                    continue;
                }

                item.SetStatus($"No se pudo actualizar su salida virtual: {exception.Message}");
                _reportError?.Invoke(item.StatusMessage, exception);
                await _virtualGamepadService.StopAsync(device.DeviceKey, cancellationToken);
                _comboEvaluator.Reset(device.DeviceKey);
                await RestoreAutoHiddenDeviceAsync(device);
                item.Update(
                    item.Record,
                    item.ConnectedDevice,
                    item.ProfileName,
                    isRunning: false,
                    ResolveVirtualGamepadType(item.Record));
                NotifyVirtualOutputStateChanged();
            }
        }

        await PollSaveNewControllerCombosAsync(cancellationToken);
    }

    /// <summary>
    /// Evalúa el combo global de «guardar mando nuevo» sobre los mandos físicos
    /// conectados que aún no tienen registro guardado. Funciona con cualquier
    /// modo de «Dispositivos no guardados», porque no depende de los registros
    /// temporales ni de la salida virtual.
    /// </summary>
    private async Task PollSaveNewControllerCombosAsync(CancellationToken cancellationToken)
    {
        NewControllerSaveCombo config = _globalPreset.SaveNewControllerCombo;
        if (_inputService is null ||
            _saveUnsavedController is null ||
            !config.Enabled ||
            config.Buttons.Count == 0)
        {
            return;
        }

        ControllerCombo combo = new()
        {
            ComboId = SaveNewControllerComboId,
            DisplayName = "Guardar mando nuevo",
            Buttons = config.Buttons,
            RequiresHold = true,
            HoldMilliseconds = config.HoldMilliseconds
        };

        long now = Environment.TickCount64;
        foreach (ControllerListItem item in _connectedControllerItems)
        {
            ConnectedGamepad? device = item.ConnectedDevice;
            if (device is null || !item.CanSave)
            {
                continue;
            }

            if (_saveComboLastReadMs.TryGetValue(device.DeviceKey, out long lastRead) &&
                now - lastRead < SaveComboPollIntervalMs)
            {
                continue;
            }

            _saveComboLastReadMs[device.DeviceKey] = now;
            GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(
                device.DeviceKey,
                cancellationToken);
            if (snapshot is null)
            {
                continue;
            }

            ComboEvaluation evaluation = _saveComboEvaluator.Evaluate(
                device.DeviceKey,
                snapshot,
                [combo]);
            if (evaluation.TriggeredCombos.Count == 0)
            {
                continue;
            }

            bool saved = await _saveUnsavedController(device);
            _notifyEvent?.Invoke(
                saved ? "Mando guardado" : "No se pudo guardar el mando",
                [saved
                    ? $"«{device.Name}» se registró como mando guardado."
                    : $"No se pudo registrar «{device.Name}». Revisa la pestaña Mandos."]);
        }
    }

    public const string BuiltinProfileName = "Predeterminado";
    private ControllerProfile? DeviceConfigBaseline(ControllerRecord record)
    {
        if (record.DeviceProfile is not null)
        {
            return record.DeviceProfile;
        }

        ControllerProfile? builtin = Profiles.FirstOrDefault(ControllerProfile.IsGlobalPreset);
        return builtin is null ? null : builtin with { ProfileId = Guid.NewGuid() };
    }


    public async Task CompleteStartupAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pendingStarts = _startupAutoStartTasks
                .Where(task => !task.IsCompleted)
                .ToArray();
            if (pendingStarts.Length == 0)
            {
                break;
            }

            await Task.WhenAll(pendingStarts);
        }

    }

    public async Task StartVirtualGamepadOnStartupAsync(ConnectedGamepad requestedDevice)
    {
        ArgumentNullException.ThrowIfNull(requestedDevice);
        VirtualGamepadControllerViewModel? item = VirtualGamepads.FirstOrDefault(candidate =>
            candidate.ConnectedDevice?.DeviceKey == requestedDevice.DeviceKey);
        if (item is null)
        {
            StatusMessage =
                "No se encontró el mando guardado que corresponde a la solicitud elevada. Guarda o identifica el mando e inténtalo de nuevo.";
            return;
        }

        await StartVirtualGamepadAsync(item);
    }

    public void ReportVirtualGamepadStatus(string status) =>
        StatusMessage = status;

    public void SetControllersUnavailable()
    {
        if (_controllerRepository is null)
        {
            return;
        }

        _controllersLoaded = false;
        StatusMessage =
            "No se pudieron comprobar los mandos guardados; no se eliminarán perfiles para evitar asignaciones colgantes.";
    }

    private async Task LoadProfilesAsync()
    {
        if (_profileRepository is null)
        {
            StatusMessage = "El almacenamiento de perfiles no está configurado.";
            _isInitialized = true;
            OnPropertyChanged(nameof(IsInitialized));
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<ControllerProfile> profiles = await _profileRepository.LoadAllAsync();
            Profiles.Clear();
            foreach (ControllerProfile profile in profiles)
            {
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault();
            await LoadGlobalPresetAsync();
            await EnsureGlobalPresetProfileAsync();
            await RemoveLegacyBuiltinProfilesAsync();
            GlobalPresetEditor ??= new GlobalPresetViewModel(
                _globalPreset,
                SaveGlobalPresetAsync);
            RebuildProfileEditorTabs();
            ControllerProfile? globalProfile = Profiles.FirstOrDefault(
                ControllerProfile.IsGlobalPreset);
            if (globalProfile is not null)
            {
                SelectedProfile = globalProfile;
            }

            RebuildAssignmentChoices();
            UpdateSelectedAssignmentChoice();
            StatusMessage = Profiles.Count == 0
                ? "Todavía no hay perfiles. Crea uno para empezar."
                : $"Se cargaron {Profiles.Count} perfil(es).";
            _isInitialized = true;
            OnPropertyChanged(nameof(IsInitialized));
            SynchronizeVirtualGamepads();
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("Los perfiles guardados no se pudieron validar.", exception);
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudieron leer los perfiles guardados.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para leer los perfiles guardados.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateAsync()
    {
        if (_profileRepository is null || IsBusy)
        {
            return;
        }

        string requestedName = DraftName.Trim();
        string name = GetUniqueName(
            string.IsNullOrWhiteSpace(requestedName) ? "Nuevo perfil" : requestedName);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var profile = new ControllerProfile
        {
            ProfileId = Guid.NewGuid(),
            DisplayName = name,
            VirtualGamepadType = VirtualGamepadTypes.IsSupported(DraftVirtualGamepadType) ||
                VirtualGamepadTypes.IsAuto(DraftVirtualGamepadType)
                ? DraftVirtualGamepadType
                : ResolveDefaultVirtualGamepadType(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await SaveNewProfileAsync(profile, "Perfil creado.");
    }

    private async Task SaveAsync()
    {
        if (_editingDeviceRecord is { } deviceRecord)
        {
            await SaveDeviceProfileAsync(deviceRecord);
            return;
        }

        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy)
        {
            return;
        }

        if (!Profiles.Contains(selected))
        {
            StatusMessage = "Selecciona un perfil del catálogo para editarlo.";
            return;
        }

        string name = DraftName.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
        {
            StatusMessage = "El nombre debe contener entre 1 y 80 caracteres.";
            return;
        }

        if (Profiles.Any(profile =>
                profile.ProfileId != selected.ProfileId &&
                string.Equals(profile.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Ya existe un perfil con ese nombre.";
            return;
        }

        if (!TryCreateCombos(out List<ControllerCombo> combos, out string? comboError))
        {
            StatusMessage = comboError ?? "No se pudieron validar los combos del perfil.";
            return;
        }

        CaptureMappingDraft(DraftVirtualGamepadType);
        string profileType = VirtualGamepadTypes.IsSupported(DraftVirtualGamepadType) ||
            VirtualGamepadTypes.IsAuto(DraftVirtualGamepadType)
            ? DraftVirtualGamepadType
            : selected.VirtualGamepadType;
        Dictionary<string, ControllerProfileMapping> typeMappings = BuildTypeMappings(selected);
        ControllerProfileMapping primaryMapping =
            typeMappings[VirtualGamepadTypes.Normalize(
                ResolveEditorVirtualGamepadType(profileType))];

        IsBusy = true;
        try
        {
            ControllerProfile updated = selected with
            {
                DisplayName = name,
                Description = DraftDescription.Trim(),
                ButtonMappings = primaryMapping.ButtonMappings,
                AxisMappings = primaryMapping.AxisMappings,
                AxisInversions = primaryMapping.AxisInversions,
                DeadZones = primaryMapping.DeadZones,
                TypeMappings = typeMappings,
                VirtualGamepadType = profileType,
                Combos = combos,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _profileRepository.SaveAsync(updated);
            ReplaceProfile(selected.ProfileId, updated);
            if (SelectedProfile?.ProfileId == selected.ProfileId)
            {
                SelectedProfile = updated;
            }

            RebuildProfileEditorTabs();
            RebuildAssignmentChoices();
            if (!string.Equals(
                    selected.VirtualGamepadType,
                    updated.VirtualGamepadType,
                    StringComparison.Ordinal))
            {
                await RestartVirtualGamepadsForProfileAsync(updated.ProfileId);
            }

            StatusMessage = "Perfil guardado.";
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("El perfil contiene datos no válidos.", exception);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo guardar el perfil.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para guardar el perfil.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveDeviceProfileAsync(ControllerRecord deviceRecord)
    {
        if (_controllerRepository is null || IsBusy)
        {
            return;
        }

        if (!TryCreateCombos(out List<ControllerCombo> combos, out string? comboError))
        {
            StatusMessage = comboError ?? "No se pudieron validar los combos de la configuración.";
            return;
        }

        ControllerProfile baseline = SelectedProfile ?? DeviceConfigBaseline(deviceRecord) ??
            new ControllerProfile
            {
                ProfileId = Guid.NewGuid(),
                DisplayName = deviceRecord.DisplayName
            };
        CaptureMappingDraft(DraftVirtualGamepadType);
        string profileType = VirtualGamepadTypes.IsSupported(DraftVirtualGamepadType) ||
            VirtualGamepadTypes.IsAuto(DraftVirtualGamepadType)
            ? DraftVirtualGamepadType
            : VirtualGamepadTypes.InferFromController(deviceRecord) ??
                VirtualGamepadTypes.Xbox360;
        Dictionary<string, ControllerProfileMapping> typeMappings = BuildTypeMappings(baseline);
        ControllerProfileMapping primaryMapping =
            typeMappings[VirtualGamepadTypes.Normalize(
                ResolveEditorVirtualGamepadType(profileType, deviceRecord))];

        IsBusy = true;
        try
        {
            ControllerProfile deviceProfile = baseline with
            {
                ProfileId = Guid.NewGuid(),
                DisplayName = "Perfil por mando",
                Description = string.Empty,
                ButtonMappings = primaryMapping.ButtonMappings,
                AxisMappings = primaryMapping.AxisMappings,
                AxisInversions = primaryMapping.AxisInversions,
                DeadZones = primaryMapping.DeadZones,
                TypeMappings = typeMappings,
                VirtualGamepadType = profileType,
                Combos = combos,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            ControllerRecord updatedRecord = deviceRecord with
            {
                ProfileId = null,
                DeviceProfile = deviceProfile,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _controllerRepository.SaveAsync(updatedRecord);
            int index = SavedControllers.IndexOf(deviceRecord);
            if (index >= 0)
            {
                SavedControllers[index] = updatedRecord;
            }

            _onControllerUpdated(updatedRecord);
            SelectedController = updatedRecord;
            RebuildAssignmentChoices();

            VirtualGamepadControllerViewModel? output = VirtualGamepads.FirstOrDefault(
                candidate => candidate.Record.ControllerId == updatedRecord.ControllerId);
            if (output?.ConnectedDevice is { } assignedDevice &&
                _virtualGamepadService?.IsRunningFor(assignedDevice.DeviceKey) == true)
            {
                string? runningType = _virtualGamepadService
                    .GetRunningVirtualGamepadType(assignedDevice.DeviceKey);
                if (runningType is not null &&
                    !string.Equals(
                        runningType,
                        ResolveVirtualGamepadType(updatedRecord),
                        StringComparison.Ordinal))
                {
                    await RestartVirtualGamepadIfRunningAsync(output);
                }
            }

            StatusMessage = "Perfil por mando guardado.";
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("La configuración del mando no se pudo validar.", exception);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo guardar la configuración del mando.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para guardar la configuración del mando.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync()
    {
        ControllerProfile? builtin = FindBuiltinProfile();
        if (builtin is null)
        {
            StatusMessage = "No se encontró el ajuste global.";
            return;
        }

        if (_editingDeviceRecord is { } deviceRecord)
        {
            await RestoreDeviceProfileAsync(deviceRecord, builtin);
            return;
        }

        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy ||
            !Profiles.Contains(selected))
        {
            return;
        }

        bool restoreFactory = IsBuiltinProfile(selected);
        ControllerProfile source = restoreFactory ? CreateFactoryProfile() : builtin;
        ControllerProfile updated = CopyProfileContent(source) with
        {
            ProfileId = selected.ProfileId,
            DisplayName = selected.DisplayName,
            CreatedAtUtc = selected.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        IsBusy = true;
        try
        {
            await _profileRepository.SaveAsync(updated);
            ReplaceProfile(selected.ProfileId, updated);
            if (SelectedProfile?.ProfileId == selected.ProfileId)
            {
                SelectedProfile = updated;
            }

            RebuildAssignmentChoices();
            if (!string.Equals(
                    selected.VirtualGamepadType,
                    updated.VirtualGamepadType,
                    StringComparison.Ordinal))
            {
                await RestartVirtualGamepadsForProfileAsync(updated.ProfileId);
            }

            StatusMessage = restoreFactory
                ? "Ajuste global restaurado a los valores de fábrica."
                : "Perfil restaurado desde «Ajuste global».";
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("El perfil contiene datos no válidos.", exception);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo restaurar el perfil.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para restaurar el perfil.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreDeviceProfileAsync(
        ControllerRecord deviceRecord,
        ControllerProfile builtin)
    {
        if (_controllerRepository is null || IsBusy)
        {
            return;
        }

        ControllerProfile deviceProfile = CopyProfileContent(builtin) with
        {
            ProfileId = Guid.NewGuid(),
            DisplayName = "Perfil por mando",
            VirtualGamepadType = VirtualGamepadTypes.InferFromController(deviceRecord) ??
                VirtualGamepadTypes.Xbox360,
            Description = string.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ControllerRecord updatedRecord = deviceRecord with
        {
            ProfileId = null,
            DeviceProfile = deviceProfile,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        IsBusy = true;
        try
        {
            await _controllerRepository.SaveAsync(updatedRecord);
            int index = SavedControllers.IndexOf(deviceRecord);
            if (index >= 0)
            {
                SavedControllers[index] = updatedRecord;
            }

            _onControllerUpdated(updatedRecord);
            SelectedController = updatedRecord;
            RebuildAssignmentChoices();

            VirtualGamepadControllerViewModel? output = VirtualGamepads.FirstOrDefault(
                candidate => candidate.Record.ControllerId == updatedRecord.ControllerId);
            if (output?.ConnectedDevice is { } assignedDevice &&
                _virtualGamepadService?.IsRunningFor(assignedDevice.DeviceKey) == true)
            {
                string? runningType = _virtualGamepadService
                    .GetRunningVirtualGamepadType(assignedDevice.DeviceKey);
                string desiredType = VirtualGamepadTypes.IsSupported(deviceProfile.VirtualGamepadType)
                    ? deviceProfile.VirtualGamepadType
                    : ResolveVirtualGamepadType(updatedRecord);
                if (runningType is not null &&
                    !string.Equals(runningType, desiredType, StringComparison.Ordinal))
                {
                    await RestartVirtualGamepadIfRunningAsync(output);
                }
            }

            StatusMessage = "Perfil por mando restaurado desde «Ajuste global».";
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("La configuración del mando no se pudo validar.", exception);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo guardar la configuración del mando.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para guardar la configuración del mando.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ControllerProfile? FindBuiltinProfile() =>
        Profiles.FirstOrDefault(ControllerProfile.IsGlobalPreset);

    private static bool IsBuiltinProfile(ControllerProfile? profile) =>
        ControllerProfile.IsGlobalPreset(profile);

    private static ControllerProfile CreateFactoryProfile() => new()
    {
        ProfileId = Guid.NewGuid(),
        DisplayName = ControllerProfile.GlobalPresetName,
        Description = ControllerProfile.GlobalPresetDescription,
        VirtualGamepadType = VirtualGamepadTypes.Default
    };

    private static ControllerProfile CopyProfileContent(ControllerProfile source) => source with
    {
        ButtonMappings = new Dictionary<string, string>(source.ButtonMappings),
        AxisMappings = new Dictionary<string, string>(source.AxisMappings),
        AxisInversions = new Dictionary<string, bool>(source.AxisInversions),
        DeadZones = new Dictionary<string, double>(source.DeadZones),
        TypeMappings = source.TypeMappings.ToDictionary(
            pair => pair.Key,
            pair => new ControllerProfileMapping
            {
                ButtonMappings = new Dictionary<string, string>(pair.Value.ButtonMappings),
                AxisMappings = new Dictionary<string, string>(pair.Value.AxisMappings),
                AxisInversions = new Dictionary<string, bool>(pair.Value.AxisInversions),
                DeadZones = new Dictionary<string, double>(pair.Value.DeadZones)
            },
            StringComparer.Ordinal),
        Combos = source.Combos.Select(combo => combo with
        {
            ComboId = Guid.NewGuid(),
            Buttons = [.. combo.Buttons],
            TargetButtons = [.. combo.TargetButtons]
        }).ToList()
    };

    private async Task ClearDeviceProfileAsync(ControllerRecord deviceRecord)
    {
        if (_controllerRepository is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ControllerRecord updatedRecord = deviceRecord with
            {
                DeviceProfile = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _controllerRepository.SaveAsync(updatedRecord);
            int index = SavedControllers.IndexOf(deviceRecord);
            if (index >= 0)
            {
                SavedControllers[index] = updatedRecord;
            }

            _onControllerUpdated(updatedRecord);
            SelectedController = updatedRecord;
            SelectedProfile = DeviceConfigBaseline(updatedRecord);
            StatusMessage = "Se restableció la configuración estándar del mando.";
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("La configuración del mando no se pudo validar.", exception);
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo guardar la configuración del mando.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para guardar la configuración del mando.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task DuplicateAsync()
    {
        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy ||
            !Profiles.Contains(selected))
        {
            return;
        }

        ControllerProfile duplicate = selected with
        {
            ProfileId = Guid.NewGuid(),
            DisplayName = GetUniqueName($"Copia de {selected.DisplayName}"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ButtonMappings = new Dictionary<string, string>(selected.ButtonMappings),
            AxisMappings = new Dictionary<string, string>(selected.AxisMappings),
            AxisInversions = new Dictionary<string, bool>(selected.AxisInversions),
            DeadZones = new Dictionary<string, double>(selected.DeadZones),
            TypeMappings = selected.TypeMappings.ToDictionary(
                pair => pair.Key,
                pair => new ControllerProfileMapping
                {
                    ButtonMappings = new Dictionary<string, string>(pair.Value.ButtonMappings),
                    AxisMappings = new Dictionary<string, string>(pair.Value.AxisMappings),
                    AxisInversions = new Dictionary<string, bool>(pair.Value.AxisInversions),
                    DeadZones = new Dictionary<string, double>(pair.Value.DeadZones)
                },
                StringComparer.Ordinal),
            Combos = selected.Combos.Select(combo => combo with
            {
                ComboId = Guid.NewGuid(),
                Buttons = [.. combo.Buttons],
                TargetButtons = [.. combo.TargetButtons]
            }).ToList()
        };
        await SaveNewProfileAsync(duplicate, "Perfil duplicado.");
    }

    private async Task DeleteAsync()
    {
        if (_editingDeviceRecord is { } deviceRecord)
        {
            await ClearDeviceProfileAsync(deviceRecord);
            return;
        }

        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy ||
            !Profiles.Contains(selected))
        {
            return;
        }

        if (IsBuiltinProfile(selected))
        {
            StatusMessage = "El ajuste global no se puede eliminar. Usa Restaurar para volver a los valores de fábrica.";
            return;
        }

        if (_controllerRepository is not null && !_controllersLoaded)
        {
            StatusMessage =
                "No se puede eliminar el perfil hasta que GDM pueda comprobar sus asignaciones a mandos.";
            return;
        }

        int assignedControllers = SavedControllers.Count(
            controller => controller.ProfileId == selected.ProfileId);
        if (assignedControllers > 0)
        {
            StatusMessage =
                $"No se puede eliminar: el perfil está asignado a {assignedControllers} mando(s). " +
                "Reasigna esos mandos primero.";
            return;
        }

        if (_confirmDelete is not null && !_confirmDelete(selected))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _profileRepository.DeleteAsync(selected.ProfileId);
            Profiles.Remove(selected);
            RebuildProfileEditorTabs();
            SelectedProfile = ProfileEditorTabs.FirstOrDefault();
            RebuildAssignmentChoices();
            StatusMessage = "Perfil eliminado.";
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo eliminar el perfil.", exception);
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("El catálogo de perfiles no se pudo validar.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para eliminar el perfil.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AssignAsync()
    {
        if (SelectedController is not { } controller ||
            SelectedAssignmentProfile is not { } choice ||
            IsBusy ||
            (_controllerRepository is null &&
             !_temporaryControllerIds.Contains(controller.ControllerId)))
        {
            return;
        }

        if (choice.ProfileId is Guid profileId &&
            !Profiles.Any(profile => profile.ProfileId == profileId))
        {
            StatusMessage = "Ese perfil ya no está disponible. Selecciona otro perfil.";
            return;
        }

        IsBusy = true;
        try
        {
            ControllerRecord updated = controller with
            {
                ProfileId = choice.ProfileId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            VirtualGamepadControllerViewModel? assignedOutput = VirtualGamepads.FirstOrDefault(
                candidate => candidate.Record.ControllerId == updated.ControllerId);
            if (_temporaryControllerIds.Contains(updated.ControllerId))
            {
                int tempIndex = _temporaryRecords.FindIndex(
                    record => record.ControllerId == updated.ControllerId);
                if (tempIndex >= 0)
                {
                    _temporaryRecords[tempIndex] = updated;
                }

                SelectedController = updated;
                assignedOutput?.UpdateRecord(updated);
            }
            else
            {
                await _controllerRepository!.SaveAsync(updated);
                int index = SavedControllers.IndexOf(controller);
                if (index >= 0)
                {
                    SavedControllers[index] = updated;
                }

                SelectedController = updated;
                _onControllerUpdated(updated);
            }
            StatusMessage = choice.ProfileId is null
                ? $"Se seleccionó el perfil por mando para «{controller.DisplayName}»."
                : $"Se asignó «{choice.DisplayName}» a «{controller.DisplayName}».";

            VirtualGamepadControllerViewModel? output = assignedOutput;
            if (output?.ConnectedDevice is { } assignedDevice &&
                _virtualGamepadService?.IsRunningFor(assignedDevice.DeviceKey) == true)
            {
                string? runningType = _virtualGamepadService
                    .GetRunningVirtualGamepadType(assignedDevice.DeviceKey);
                string desiredType = ResolveVirtualGamepadType(updated);
                if (runningType is not null &&
                    !string.Equals(runningType, desiredType, StringComparison.Ordinal))
                {
                    await RestartVirtualGamepadIfRunningAsync(output);
                }
            }
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("La configuración del mando no se pudo validar.", exception);
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo guardar la asignación del mando.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para guardar la asignación del mando.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveNewProfileAsync(ControllerProfile profile, string successMessage)
    {
        if (_profileRepository is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _profileRepository.SaveAsync(profile);
            Profiles.Add(profile);
            RebuildProfileEditorTabs();
            SelectedProfile = profile;
            RebuildAssignmentChoices();
            StatusMessage = successMessage;
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("El perfil contiene datos no válidos.", exception);
        }
        catch (InvalidOperationException exception)
        {
            StatusMessage = exception.Message;
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo guardar el perfil.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para guardar el perfil.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string GetUniqueName(string baseName)
    {
        string candidate = baseName.Length <= 80 ? baseName : baseName[..80];
        int suffix = 2;
        while (Profiles.Any(profile =>
                   string.Equals(profile.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            string suffixText = $" ({suffix++})";
            candidate = $"{baseName[..Math.Min(baseName.Length, 80 - suffixText.Length)]}{suffixText}";
        }

        return candidate;
    }

    private void ReplaceProfile(Guid profileId, ControllerProfile updated)
    {
        int index = -1;
        for (int candidate = 0; candidate < Profiles.Count; candidate++)
        {
            if (Profiles[candidate].ProfileId == profileId)
            {
                index = candidate;
                break;
            }
        }

        if (index >= 0)
        {
            Profiles[index] = updated;
        }
    }

    private void RebuildProfileEditorTabs()
    {
        ProfileEditorTabs.Clear();
        ControllerProfile? global = Profiles.FirstOrDefault(ControllerProfile.IsGlobalPreset);
        if (global is not null)
        {
            ProfileEditorTabs.Add(global);
        }

        foreach (ControllerProfile profile in Profiles)
        {
            if (!ControllerProfile.IsGlobalPreset(profile))
            {
                ProfileEditorTabs.Add(profile);
            }
        }

        _selectedProfileTab = _selectedProfile is not null &&
            ProfileEditorTabs.Contains(_selectedProfile)
                ? _selectedProfile
                : ProfileEditorTabs.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedProfileTab));
    }

    private void RebuildAssignmentChoices()
    {
        Guid? selectedId = SelectedAssignmentProfile?.ProfileId;
        AssignmentChoices.Clear();
        ControllerProfile? global = Profiles.FirstOrDefault(ControllerProfile.IsGlobalPreset);
        if (global is not null)
        {
            AssignmentChoices.Add(new ProfileChoice(global.ProfileId, global.DisplayName));
        }

        AssignmentChoices.Add(new ProfileChoice(null, "Perfil por mando"));
        foreach (ControllerProfile profile in Profiles)
        {
            if (ControllerProfile.IsGlobalPreset(profile))
            {
                continue;
            }

            AssignmentChoices.Add(new ProfileChoice(profile.ProfileId, profile.DisplayName));
        }

        if (SelectedController?.ProfileId is Guid currentId &&
            !Profiles.Any(profile => profile.ProfileId == currentId))
        {
            AssignmentChoices.Add(new ProfileChoice(currentId, "Perfil no disponible"));
        }

        SelectedAssignmentProfile = AssignmentChoices.FirstOrDefault(
            choice => choice.ProfileId == selectedId);
        if (SelectedAssignmentProfile is null)
        {
            UpdateSelectedAssignmentChoice();
        }
    }

    private ControllerProfileMapping GetMappingDraft(ControllerProfile? profile, string type)
    {
        string normalized = ResolveEditorVirtualGamepadType(type);
        return _typeMappingDrafts.TryGetValue(normalized, out ControllerProfileMapping? draft)
            ? draft
            : profile?.GetMappingsFor(normalized) ?? new ControllerProfileMapping();
    }

    private void CaptureMappingDraft(string type)
    {
        string normalized = ResolveEditorVirtualGamepadType(type);
        ControllerProfileMapping existing = GetMappingDraft(SelectedProfile, normalized);
        _typeMappingDrafts[normalized] = new ControllerProfileMapping
        {
            ButtonMappings = CreateMappingDictionary(existing.ButtonMappings, ButtonMappingRows),
            AxisMappings = CreateMappingDictionary(existing.AxisMappings, AxisMappingRows),
            AxisInversions = CreateInversionDictionary(existing.AxisInversions, AxisMappingRows),
            DeadZones = CreateDeadZoneDictionary(
                existing.DeadZones,
                LeftStickDeadZone,
                RightStickDeadZone)
        };
    }

    private Dictionary<string, ControllerProfileMapping> BuildTypeMappings(ControllerProfile selected)
    {
        var mappings = new Dictionary<string, ControllerProfileMapping>(StringComparer.Ordinal);
        foreach (VirtualGamepadTypeOption option in VirtualGamepadTypeOptions.Where(
                     candidate => VirtualGamepadTypes.IsSupported(candidate.Id)))
        {
            mappings[option.Id] = _typeMappingDrafts.TryGetValue(option.Id, out ControllerProfileMapping? draft)
                ? draft
                : selected.GetMappingsFor(option.Id);
        }

        return mappings;
    }

    private void LoadMappingRows(ControllerProfile? profile)
    {
        LoadControlMappingRows(GetMappingDraft(profile, DraftVirtualGamepadType));

        ComboRows.Clear();
        foreach (ControllerCombo combo in profile?.Combos ?? [])
        {
            ComboRows.Add(new ProfileComboRow(
                combo.ComboId,
                combo.DisplayName,
                combo.Buttons,
                combo.RequiresHold,
                combo.HoldMilliseconds,
                combo.ActionType,
                combo.TargetButtons));
        }

        RefreshComboTargetButtons();
    }

    private void LoadControlMappingRows(ControllerProfileMapping? mapping)
    {
        ButtonMappingRows.Clear();
        foreach (GamepadControlOption source in GamepadControlCatalog.Buttons)
        {
            string target = mapping?.ButtonMappings.TryGetValue(source.Id, out string? mappedTarget) == true
                ? NormalizeButtonTarget(mappedTarget, source.Id)
                : source.Id;
            ButtonMappingRows.Add(new ProfileMappingRow(
                source,
                GamepadControlCatalog.GetButtonTargetsFor(DraftVirtualGamepadType),
                target));
        }

        AxisMappingRows.Clear();
        foreach (GamepadControlOption source in GamepadControlCatalog.Axes)
        {
            string target = mapping?.AxisMappings.TryGetValue(source.Id, out string? mappedTarget) == true
                ? NormalizeAxisTarget(mappedTarget, source.Id)
                    : source.Id;
            AxisMappingRows.Add(new ProfileMappingRow(
                source,
                GamepadControlCatalog.Axes,
                target,
                mapping?.AxisInversions.TryGetValue(source.Id, out bool isInverted) == true &&
                    isInverted));
        }

        LeftStickDeadZone = mapping?.DeadZones.GetValueOrDefault("leftStick") ?? 0d;
        RightStickDeadZone = mapping?.DeadZones.GetValueOrDefault("rightStick") ?? 0d;
    }

    private void RefreshComboTargetButtons()
    {
        IReadOnlyList<GamepadControlOption> targets =
            GamepadControlCatalog.GetButtonTargetsFor(
                ResolveEditorVirtualGamepadType(DraftVirtualGamepadType));
        foreach (ProfileComboRow row in ComboRows)
        {
            row.RebuildTargetButtons(targets);
        }
    }

    private string ResolveDefaultVirtualGamepadType() =>
        ResolveDefaultVirtualGamepadType(null);

    private string ResolveDefaultVirtualGamepadType(ControllerRecord? context)
    {
        string? inferred = VirtualGamepadTypes.InferFromController(context ?? SelectedController);
        if (inferred is not null)
        {
            return inferred;
        }

        string? defaultType = _defaultVirtualGamepadType?.Invoke();
        if (VirtualGamepadTypes.IsAuto(defaultType))
        {
            return VirtualGamepadTypes.Xbox360;
        }

        return VirtualGamepadTypes.IsSupported(defaultType)
            ? defaultType!
            : VirtualGamepadTypes.Default;
    }

    private ControllerRecord? EditorContextRecord() =>
        _editingDeviceRecord ?? SelectedController;

    private string ResolveEditorVirtualGamepadType(string? type, ControllerRecord? context = null)
    {
        if (VirtualGamepadTypes.IsAuto(type))
        {
            return VirtualGamepadTypes.InferFromController(context ?? EditorContextRecord()) ??
                VirtualGamepadTypes.Xbox360;
        }

        return VirtualGamepadTypes.Normalize(type);
    }

    public string ResolveVirtualGamepadType(ControllerRecord record)
    {
        string? overrideType = record.SettingOverrides?.VirtualGamepadType;
        if (VirtualGamepadTypes.IsSupported(overrideType))
        {
            return overrideType!;
        }

        string? profileType = FindAssignedProfile(record)?.VirtualGamepadType;
        if (VirtualGamepadTypes.IsAuto(profileType))
        {
            return VirtualGamepadTypes.InferFromController(record) ??
                VirtualGamepadTypes.Xbox360;
        }

        return VirtualGamepadTypes.IsSupported(profileType)
            ? profileType!
            : ResolveDefaultVirtualGamepadType(record);
    }

    private bool TryCreateCombos(out List<ControllerCombo> combos, out string? error)
    {
        combos = [];
        error = null;
        int index = 0;
        foreach (ProfileComboRow row in ComboRows)
        {
            index++;
            string comboName = string.IsNullOrWhiteSpace(row.DisplayName)
                ? $"Combo {index}"
                : row.DisplayName.Trim();
            IReadOnlyList<string> triggers = row.SelectedTriggerButtonIds;
            if (triggers.Count == 0)
            {
                error = $"El combo «{comboName}» no tiene botones activadores seleccionados.";
                return false;
            }

            int holdMilliseconds = 0;
            if (row.RequiresHold)
            {
                if (!int.TryParse(
                        row.HoldMilliseconds,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out holdMilliseconds) ||
                    holdMilliseconds is < 1 or > 10000)
                {
                    error = $"El combo «{comboName}» debe mantener los botones entre 1 y 10000 ms.";
                    return false;
                }
            }

            string actionType = ComboActionTypes.Normalize(row.ActionType);
            List<string> targetButtons = actionType == ComboActionTypes.Buttons
                ? row.SelectedTargetButtonIds
                    .Where(id => GamepadControlCatalog.Buttons.Any(button => button.Id == id))
                    .ToList()
                : [];
            if (actionType == ComboActionTypes.Buttons && targetButtons.Count == 0)
            {
                error = $"El combo «{comboName}» no tiene botones de salida seleccionados.";
                return false;
            }

            combos.Add(new ControllerCombo
            {
                ComboId = row.ComboId == Guid.Empty ? Guid.NewGuid() : row.ComboId,
                DisplayName = comboName,
                Buttons = [.. triggers],
                RequiresHold = row.RequiresHold,
                HoldMilliseconds = holdMilliseconds,
                ActionType = actionType,
                TargetButtons = targetButtons
            });
        }

        return true;
    }

    private static Dictionary<string, string> CreateMappingDictionary(
        IReadOnlyDictionary<string, string> existingMappings,
        IEnumerable<ProfileMappingRow> rows)
    {
        var mappings = existingMappings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (ProfileMappingRow row in rows)
        {
            mappings.Remove(row.SourceId);
            if (!string.Equals(row.SourceId, row.SelectedTargetId, StringComparison.Ordinal))
            {
                mappings[row.SourceId] = row.SelectedTargetId;
            }
        }

        return mappings;
    }

    private static string NormalizeButtonTarget(string target, string sourceId)
    {
        string normalized = target switch
        {
            "none" => "none",
            "A" => "south",
            "B" => "east",
            "X" => "west",
            "Y" => "north",
            _ => target
        };
        return normalized == GamepadControlCatalog.NoButtonTarget.Id ||
               GamepadControlCatalog.Buttons.Any(button => button.Id == normalized)
            ? normalized
            : sourceId;
    }

    private static string NormalizeAxisTarget(string target, string sourceId)
    {
        string normalized = target switch
        {
            "X" or "LX" => "leftX",
            "Y" or "LY" => "leftY",
            "RX" => "rightX",
            "RY" => "rightY",
            "LT" => "leftTrigger",
            "RT" => "rightTrigger",
            _ => target
        };
        return GamepadControlCatalog.Axes.Any(axis => axis.Id == normalized)
            ? normalized
            : sourceId;
    }

    private static Dictionary<string, bool> CreateInversionDictionary(
        IReadOnlyDictionary<string, bool> existingInversions,
        IEnumerable<ProfileMappingRow> rows)
    {
        var inversions = existingInversions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (ProfileMappingRow row in rows)
        {
            inversions.Remove(row.SourceId);
            if (row.IsInverted)
            {
                inversions[row.SourceId] = true;
            }
        }

        return inversions;
    }

    private static Dictionary<string, double> CreateDeadZoneDictionary(
        IReadOnlyDictionary<string, double> existingDeadZones,
        double leftStick,
        double rightStick)
    {
        var deadZones = existingDeadZones.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        SetDeadZone("leftStick", leftStick);
        SetDeadZone("rightStick", rightStick);
        return deadZones;

        void SetDeadZone(string name, double value)
        {
            if (value <= 0d)
            {
                deadZones.Remove(name);
            }
            else
            {
                deadZones[name] = value;
            }
        }
    }

    private void SynchronizeVirtualGamepads()
    {
        SynchronizeTemporaryControllers();
        var knownIds = SavedControllers
            .Select(controller => controller.ControllerId)
            .Concat(_temporaryRecords.Select(controller => controller.ControllerId))
            .ToHashSet();
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads.ToArray())
        {
            if (knownIds.Contains(item.Record.ControllerId))
            {
                continue;
            }

            if (item.ConnectedDevice is { } oldDevice &&
                _virtualGamepadService?.IsRunningFor(oldDevice.DeviceKey) == true)
            {
                _ = StopVirtualGamepadAsync(item);
            }

            VirtualGamepads.Remove(item);
        }

        foreach (ControllerRecord record in SavedControllers.Concat(_temporaryRecords))
        {
            bool isTemporary = _temporaryControllerIds.Contains(record.ControllerId);
            ControllerListItem? controllerItem = isTemporary
                ? null
                : _connectedControllerItems.FirstOrDefault(
                    candidate => candidate.SavedRecord?.ControllerId == record.ControllerId);
            ConnectedGamepad? connectedDevice = controllerItem?.ConnectedDevice ??
                (isTemporary
                    ? _allConnectedDevices.FirstOrDefault(
                        device => device.DeviceKey ==
                            _temporaryDeviceKeys.GetValueOrDefault(record.ControllerId))
                    : null);
            if (connectedDevice is not null &&
                _phantomDeviceKeys.Contains(connectedDevice.DeviceKey))
            {
                if (IsPhysicallyPresent(connectedDevice))
                {
                    _phantomDeviceKeys.Remove(connectedDevice.DeviceKey);
                }
                else
                {
                    connectedDevice = null;
                }
            }

            VirtualGamepadControllerViewModel? output = VirtualGamepads.FirstOrDefault(
                candidate => candidate.Record.ControllerId == record.ControllerId);
            if (output is null)
            {
                output = new VirtualGamepadControllerViewModel(
                    record,
                    StartVirtualGamepadAsync,
                    StopVirtualGamepadNotifyingAsync,
                    ToggleAutoStartVirtualGamepadAsync,
                    ToggleAutoHidePhysicalGamepadAsync,
                    PowerOffControllerAsync,
                    SavePowerOffSettingsAsync,
                    SaveLedSettingsAsync,
                    SaveVibrationSettingsAsync,
                    SaveInputPollIntervalSettingsAsync,
                    SaveMotionSettingsAsync,
                    SaveMotionIntervalSettingsAsync,
                    CalibrateMotionAsync,
                    SaveMotionInvertSettingsAsync,
                    SaveBatteryAlertSettingsAsync,
                    EffectiveSettings,
                    ResetSettingOverridesAsync,
                    SaveBoolOverrideAsync,
                    SaveOverrideValueAsync);
                output.SetTemporary(isTemporary);
                VirtualGamepads.Add(output);
                output.PropertyChanged += OnVirtualGamepadItemPropertyChanged;
            }

            ConnectedGamepad? previousDevice = output.ConnectedDevice;
            bool running = connectedDevice is not null &&
                _virtualGamepadService?.IsRunningFor(connectedDevice.DeviceKey) == true;
            if (previousDevice is not null &&
                (connectedDevice is null || previousDevice.DeviceKey != connectedDevice.DeviceKey) &&
                _virtualGamepadService?.IsRunningFor(previousDevice.DeviceKey) == true)
            {
                if (_autoHiddenDeviceKeys.Contains(previousDevice.DeviceKey))
                {
                    bool samePhysicalDevice = connectedDevice is not null &&
                        IsSamePhysicalDevice(previousDevice, connectedDevice);
                    bool rebound = samePhysicalDevice &&
                        _virtualGamepadService.RebindDevice(previousDevice.DeviceKey, connectedDevice!);
                    string rebindState = $"{previousDevice.DeviceKey}->{connectedDevice?.DeviceKey ?? "(ausente)"}";
                    if (_lastRebindDiagnostics.GetValueOrDefault(record.ControllerId) != rebindState)
                    {
                        _lastRebindDiagnostics[record.ControllerId] = rebindState;
                        WriteAutoHideDiagnostic(
                            samePhysicalDevice && rebound
                                ? DiagnosticLogLevel.Information
                                : DiagnosticLogLevel.Warning,
                            $"Reconciliación de mando oculto. Controlador={record.ControllerId}; " +
                            $"anterior={DescribeDevice(previousDevice)}; " +
                            $"candidato={(connectedDevice is null ? "(ninguno)" : DescribeDevice(connectedDevice))}; " +
                            $"mismaIdentidad={samePhysicalDevice}; reasociado={rebound}.");
                    }

                    if (rebound && connectedDevice is not null)
                    {
                        _autoHiddenDeviceKeys.Remove(previousDevice.DeviceKey);
                        _autoHiddenDeviceKeys.Add(connectedDevice.DeviceKey);
                        _autoHiddenPresenceStates.Remove(previousDevice.DeviceKey);
                        _hiddenInputUnavailableStates.Remove(previousDevice.DeviceKey);
                        connectedDevice = connectedDevice with { IsHiddenByGdm = true };
                        running = true;
                    }
                    else
                    {
                        connectedDevice = previousDevice;
                        running = true;
                    }
                }
                else
                {
                    _ = StopVirtualGamepadAsync(output);
                    running = false;
                }
            }

            string profileName = FindAssignedProfile(record)?.DisplayName ?? "Perfil por mando";
            output.Update(
                record,
                connectedDevice,
                profileName,
                running,
                ResolveVirtualGamepadType(record));
            _ = ApplyLedStateAsync(output);
            RefreshPowerOffSupportIfNeeded(output, connectedDevice);
            if (_isInitialized &&
                _controllersLoaded &&
                EffectiveSettings(record).AutoStartVirtualGamepad &&
                connectedDevice is not null &&
                !running &&
                !output.IsStarting &&
                !_autoStartSuppressed.Contains(record.ControllerId) &&
                _autoStartAttempts.GetValueOrDefault(record.ControllerId) != connectedDevice.DeviceKey)
            {
                _autoStartAttempts[record.ControllerId] = connectedDevice.DeviceKey;
                _startupAutoStartTasks.Add(StartVirtualGamepadAsync(output));
            }

            if (connectedDevice is null)
            {
                _autoStartAttempts.Remove(record.ControllerId);
                _autoStartSuppressed.Remove(record.ControllerId);
            }
        }

        object? desiredSelection = _selectedVirtualGamepad is GlobalPresetViewModel
            ? _selectedVirtualGamepad
            : VirtualGamepads.FirstOrDefault(
                    item => item.Record.ControllerId == SelectedController?.ControllerId) ??
                (object?)VirtualGamepads.FirstOrDefault(item => !item.IsTemporary) ??
                VirtualGamepads.FirstOrDefault();
        if (!ReferenceEquals(_selectedVirtualGamepad, desiredSelection))
        {
            SelectedVirtualGamepad = desiredSelection;
        }

        RebuildPerfilesTabs();
        if (desiredSelection is not null &&
            !ReferenceEquals(_selectedVirtualGamepad, desiredSelection))
        {
            SelectedVirtualGamepad = desiredSelection;
        }

        OnPropertyChanged(nameof(HasVirtualGamepads));
        OnPropertyChanged(nameof(ShowNoVirtualGamepads));
        OnPropertyChanged(nameof(IsAnyVirtualGamepadBusy));
        EnsureBatteryEffectTimer();
        EnsureLedGuardTimer();
    }

    private void RebuildPerfilesTabs()
    {
        var desired = new List<object>();
        if (GlobalPresetEditor is not null)
        {
            desired.Add(GlobalPresetEditor);
        }

        bool showTemporary =
            _globalPreset.UnsavedDevices == GlobalPresetUnsavedMode.TempTab;
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads)
        {
            if (!item.IsTemporary || showTemporary)
            {
                desired.Add(item);
            }
        }

        if (PerfilesTabs.Count == desired.Count &&
            PerfilesTabs.SequenceEqual(desired))
        {
            return;
        }

        PerfilesTabs.Clear();
        foreach (object item in desired)
        {
            PerfilesTabs.Add(item);
        }
    }

    private void SynchronizeTemporaryControllers()
    {
        if (_globalPreset.UnsavedDevices == GlobalPresetUnsavedMode.None ||
            !_isInitialized ||
            !_controllersLoaded)
        {
            if (_temporaryRecords.Count > 0)
            {
                _temporaryRecords.Clear();
                _temporaryControllerIds.Clear();
                _temporaryDeviceKeys.Clear();
            }

            return;
        }

        var wanted = new List<(ControllerRecord Record, ConnectedGamepad Device)>();
        foreach (ControllerListItem item in _connectedControllerItems)
        {
            ConnectedGamepad? device = item.ConnectedDevice;
            if (device is null ||
                device.IsVirtualGamepad ||
                item.SavedRecord is not null)
            {
                continue;
            }

            Guid id = GetTemporaryControllerId(device);
            ControllerRecord? existing = _temporaryRecords.FirstOrDefault(
                record => record.ControllerId == id);
            wanted.Add((existing ?? CreateTemporaryRecord(device, id), device));
        }

        _temporaryRecords.RemoveAll(record =>
            !wanted.Any(pair => pair.Record.ControllerId == record.ControllerId));
        _temporaryControllerIds.Clear();
        _temporaryDeviceKeys.Clear();
        foreach ((ControllerRecord record, ConnectedGamepad device) in wanted)
        {
            _temporaryControllerIds.Add(record.ControllerId);
            _temporaryDeviceKeys[record.ControllerId] = device.DeviceKey;
        }
    }

    private static Guid GetTemporaryControllerId(ConnectedGamepad device)
    {
        string key = device.SerialNumber ??
            $"{device.Name}|{device.VendorId:X4}|{device.ProductId:X4}|{device.SdlGuid}";
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("gdm-temp|" + key),
            hash);
        return new Guid(hash[..16]);
    }

    private static ControllerRecord CreateTemporaryRecord(
        ConnectedGamepad device,
        Guid controllerId)
    {
        var evidence = new List<ControllerIdentityEvidence>();
        if (device.VendorId is ushort vendorId)
        {
            evidence.Add(new ControllerIdentityEvidence
            {
                Source = "SDL2",
                Kind = "VendorId",
                Value = vendorId.ToString("X4")
            });
        }

        if (device.ProductId is ushort productId)
        {
            evidence.Add(new ControllerIdentityEvidence
            {
                Source = "SDL2",
                Kind = "ProductId",
                Value = productId.ToString("X4")
            });
        }

        if (!string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            evidence.Add(new ControllerIdentityEvidence
            {
                Source = "SDL2",
                Kind = "SerialNumber",
                Value = device.SerialNumber
            });
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ControllerRecord
        {
            ControllerId = controllerId,
            DisplayName = $"{device.Name} (temporal)",
            Identity = new ControllerIdentity
            {
                Model = device.Name,
                Evidence = evidence
            },
            ProfileId = ControllerProfile.GlobalPresetId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    public int EffectiveInputPollIntervalMs(ControllerRecord record)
    {
        int configured = EffectiveSettings(record).InputPollIntervalMs;
        int interval = configured > 0
            ? configured
            : _defaultInputPollIntervalMs?.Invoke() ?? 8;
        return Math.Clamp(interval, 4, 100);
    }

    private static GamepadInputSnapshot ApplyMotionBias(
        ControllerRecord record,
        GamepadInputSnapshot snapshot)
    {
        ControllerSettings settings = record.Settings;
        bool needsAdjust = settings.MotionCalibrated ||
            settings.MotionInvertGyroPitch || settings.MotionInvertGyroYaw ||
            settings.MotionInvertGyroRoll;
        if (!needsAdjust ||
            snapshot.Motion is not { HasGyroscope: true } motion)
        {
            return snapshot;
        }

        double pitch = motion.GyroPitchRadS - settings.MotionGyroBiasPitchRadS;
        double yaw = motion.GyroYawRadS - settings.MotionGyroBiasYawRadS;
        double roll = motion.GyroRollRadS - settings.MotionGyroBiasRollRadS;
        if (settings.MotionInvertGyroPitch)
        {
            pitch = -pitch;
        }

        if (settings.MotionInvertGyroYaw)
        {
            yaw = -yaw;
        }

        if (settings.MotionInvertGyroRoll)
        {
            roll = -roll;
        }

        return snapshot with
        {
            Motion = motion with
            {
                GyroPitchRadS = pitch,
                GyroYawRadS = yaw,
                GyroRollRadS = roll
            }
        };
    }

    public int EffectiveMotionPollIntervalMs(ControllerRecord record)
    {
        int configured = EffectiveSettings(record).MotionPollIntervalMs;
        int interval = configured > 0
            ? configured
            : EffectiveInputPollIntervalMs(record);
        return Math.Clamp(interval, 4, 100);
    }

    public int MinRunningVirtualPollIntervalMs()
    {
        int min = int.MaxValue;
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads)
        {
            if (item.IsRunning && item.ConnectedDevice is not null)
            {
                min = Math.Min(min, EffectiveInputPollIntervalMs(item.Record));
            }
        }

        return min;
    }

    private void OnVirtualGamepadItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VirtualGamepadControllerViewModel.InputPollIntervalMs))
        {
            VirtualPollIntervalChanged?.Invoke(this, EventArgs.Empty);
        }

        if (e.PropertyName == nameof(VirtualGamepadControllerViewModel.IsStarting))
        {
            OnPropertyChanged(nameof(IsAnyVirtualGamepadBusy));
        }
    }

    private static bool IsSamePhysicalDevice(
        ConnectedGamepad previousDevice,
        ConnectedGamepad currentDevice)
    {
        if (!string.IsNullOrWhiteSpace(previousDevice.DevicePath) &&
            string.Equals(
                previousDevice.DevicePath,
                currentDevice.DevicePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(previousDevice.SerialNumber) &&
            !string.IsNullOrWhiteSpace(currentDevice.SerialNumber))
        {
            return string.Equals(
                    previousDevice.SerialNumber,
                    currentDevice.SerialNumber,
                    StringComparison.Ordinal) &&
                previousDevice.VendorId == currentDevice.VendorId &&
                previousDevice.ProductId == currentDevice.ProductId;
        }

        return string.IsNullOrWhiteSpace(previousDevice.SerialNumber) &&
            string.IsNullOrWhiteSpace(currentDevice.SerialNumber) &&
            previousDevice.VendorId is not null &&
            previousDevice.VendorId == currentDevice.VendorId &&
            previousDevice.ProductId is not null &&
            previousDevice.ProductId == currentDevice.ProductId &&
            string.Equals(previousDevice.Name, currentDevice.Name, StringComparison.Ordinal);
    }

    private async Task StartVirtualGamepadAsync(VirtualGamepadControllerViewModel item)
    {
        if (_inputService is null || _virtualGamepadService is null)
        {
            item.SetStatus("No están disponibles SDL o el servicio de mando virtual.");
            return;
        }

        if (item.ConnectedDevice is not { } device)
        {
            item.SetStatus("Conecta el mando físico antes de iniciar su salida virtual.");
            return;
        }

        if (item.IsStarting || _virtualGamepadService.IsRunningFor(device.DeviceKey))
        {
            return;
        }

        _autoStartSuppressed.Remove(item.Record.ControllerId);
        item.SetStarting(true);
        try
        {
            ControllerSettings startSettings = EffectiveSettings(item.Record);
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Inicio de salida virtual. Controlador={item.Record.ControllerId}; " +
                $"autoInicio={startSettings.AutoStartVirtualGamepad}; " +
                $"autoOcultar={startSettings.AutoHidePhysicalGamepad}; " +
                $"reinicioPnP={startSettings.RestartPhysicalDeviceOnVirtualStart}; mando={DescribeDevice(device)}.");

            if (startSettings.RestartPhysicalDeviceOnVirtualStart &&
                !string.IsNullOrWhiteSpace(device.DevicePath))
            {
                // Ocultar primero: mientras el físico esté en lista negra, ningún
                // programa puede reabrirlo durante el ciclo de reinicio PnP.
                await TryAutoHidePhysicalDeviceAsync(item, device);
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Reinicio PnP del mando físico solicitado antes de crear la salida. " +
                    $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
                bool restarted = await PhysicalDeviceRestartService.RestartAsync(device.DevicePath);
                if (!restarted)
                {
                    item.SetStatus(
                        "No se pudo reiniciar el dispositivo físico en Windows; la salida virtual no se inició.");
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Warning,
                        $"El reinicio PnP del mando físico falló. " +
                        $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
                    return;
                }

                ConnectedGamepad? reconnected =
                    await WaitForPhysicalReenumerationAsync(item, device);
                if (reconnected is null)
                {
                    item.SetStatus(
                        "El mando físico no volvió a aparecer tras el reinicio; inténtalo de nuevo.");
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Warning,
                        $"El mando físico no se re-enumeró tras el reinicio PnP. " +
                        $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
                    return;
                }

                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Mando físico re-enumerado tras el reinicio PnP. " +
                    $"Controlador={item.Record.ControllerId}; anterior={device.DeviceKey}; " +
                    $"nuevo={reconnected.DeviceKey}; mando={DescribeDevice(reconnected)}.");
                device = reconnected;
            }

            GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(device.DeviceKey);
            if (snapshot is null)
            {
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Warning,
                    $"La lectura inicial de SDL devolvió null antes de crear la salida virtual. " +
                    $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            }

            if (item.ConnectedDevice?.DeviceKey != device.DeviceKey)
            {
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Warning,
                    $"El dispositivo cambió durante el inicio de la salida. " +
                    $"Controlador={item.Record.ControllerId}; claveInicial={device.DeviceKey}; " +
                    $"claveActual={item.ConnectedDevice?.DeviceKey ?? "(ausente)"}.");
                item.SetStatus("El mando físico se desconectó o cambió durante el inicio.");
                return;
            }

            if (snapshot is null)
            {
                item.SetStatus("El mando físico se desconectó; actualiza la lista e inténtalo de nuevo.");
                return;
            }

            if (!snapshot.IsStandardGameController)
            {
                item.SetStatus("SDL no tiene un mapeo estándar para este mando físico.");
                return;
            }

            ControllerProfile? profile = FindAssignedProfile(item.Record);
            string virtualGamepadType = ResolveVirtualGamepadType(item.Record);
            await _virtualGamepadService.StartAsync(
                device,
                snapshot,
                profile?.WithMappingsFor(virtualGamepadType),
                virtualGamepadType);
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Salida virtual iniciada. Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}; " +
                $"mapeoEstandar={snapshot.IsStandardGameController}; ejes={snapshot.Axes.Count}; botones={snapshot.Buttons.Count}.");
            item.SetStatus(
                $"Mando virtual {VirtualGamepadTypes.DisplayName(virtualGamepadType)} activo.");
            item.Update(
                item.Record,
                device,
                profile?.DisplayName ?? "Perfil por mando",
                isRunning: true,
                virtualGamepadType);
            NotifyVirtualOutputStateChanged();
            NotifyVirtualGamepadStarted(device, virtualGamepadType, profile);
            await TryAutoHidePhysicalDeviceAsync(item, device);
            _ = ApplyLedStateAsync(item);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (!_virtualGamepadService.IsAdministrator &&
                _elevationRequested)
            {
                item.SetStatus("GDM se volverá a abrir como administrador para iniciar esta salida.");
            }
            else if (!_virtualGamepadService.IsAdministrator &&
                     _requestElevatedVirtualGamepad?.Invoke(device) == true)
            {
                _elevationRequested = true;
                item.SetStatus("GDM se volverá a abrir como administrador para iniciar esta salida.");
            }
            else
            {
                item.SetStatus($"No se pudo iniciar la salida virtual: {exception.Message}");
                _reportError?.Invoke(item.StatusMessage, exception);
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException or
            InvalidOperationException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            item.SetStatus($"No se pudo iniciar la salida virtual: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
        finally
        {
            item.SetStarting(false);
        }
    }

    private async Task<ConnectedGamepad?> WaitForPhysicalReenumerationAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad previousDevice)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ConnectedGamepad? current = item.ConnectedDevice;
            if (current is not null &&
                current.DeviceKey != previousDevice.DeviceKey &&
                IsSamePhysicalDevice(previousDevice, current))
            {
                return current;
            }

            await Task.Delay(150);
        }

        return null;
    }

    public async Task RestartVirtualGamepadsForProfileAsync(Guid profileId)
    {
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads
                     .Where(candidate => candidate.Record.ProfileId == profileId)
                     .ToArray())
        {
            await RestartVirtualGamepadIfRunningAsync(item);
        }
    }

    public async Task RestartVirtualGamepadsForDefaultTypeAsync()
    {
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads
                     .Where(candidate => FindAssignedProfile(candidate.Record) is null)
                     .ToArray())
        {
            await RestartVirtualGamepadIfRunningAsync(item);
        }
    }

    private async Task RestartVirtualGamepadIfRunningAsync(VirtualGamepadControllerViewModel item)
    {
        if (_virtualGamepadService is null ||
            item.ConnectedDevice is not { } device ||
            !_virtualGamepadService.IsRunningFor(device.DeviceKey))
        {
            return;
        }

        await StopVirtualGamepadAsync(item);
        if (item.ConnectedDevice?.DeviceKey == device.DeviceKey &&
            !_virtualGamepadService.IsRunningFor(device.DeviceKey))
        {
            await StartVirtualGamepadAsync(item);
        }
    }

    private Task StopVirtualGamepadAsync(VirtualGamepadControllerViewModel item) =>
        StopVirtualGamepadCoreAsync(item, updateStatus: true);

    /// <summary>
    /// Parada iniciada por el usuario (botón «Detener» o combo de alternar
    /// salida): además de detener notifica cómo volver a encenderla.
    /// Las paradas automáticas (desconexión, errores) no notifican.
    /// </summary>
    private Task StopVirtualGamepadNotifyingAsync(VirtualGamepadControllerViewModel item) =>
        StopVirtualGamepadCoreAsync(item, updateStatus: true, notifyUser: true);

    /// <summary>
    /// Detiene la salida virtual de un mando guardado (si está activa) antes de
    /// eliminar su registro: usa el mismo flujo que el botón «Detener», que
    /// además restaura el ocultamiento de HidHide del mando físico.
    /// </summary>
    public async Task StopVirtualOutputForControllerAsync(Guid controllerId)
    {
        VirtualGamepadControllerViewModel? item = VirtualGamepads.FirstOrDefault(
            candidate => candidate.Record.ControllerId == controllerId);
        if (item is not null)
        {
            await StopVirtualGamepadSilentlyAsync(item);
        }
    }

    private Task StopVirtualGamepadSilentlyAsync(VirtualGamepadControllerViewModel item) =>
        StopVirtualGamepadCoreAsync(item, updateStatus: false);

    private async Task StopVirtualGamepadCoreAsync(
        VirtualGamepadControllerViewModel item,
        bool updateStatus,
        bool notifyUser = false)
    {
        if (_virtualGamepadService is null ||
            item.ConnectedDevice is not { } device ||
            !_virtualGamepadService.IsRunningFor(device.DeviceKey))
        {
            return;
        }

        item.SetStarting(true);
        try
        {
            await _virtualGamepadService.StopAsync(device.DeviceKey);
            await RestoreAutoHiddenDeviceAsync(device);

            if (updateStatus)
            {
                item.SetStatus("La salida virtual de este mando está detenida.");
            }

            item.Update(
                item.Record,
                item.ConnectedDevice,
                item.ProfileName,
                isRunning: false,
                ResolveVirtualGamepadType(item.Record));
            NotifyVirtualOutputStateChanged();
            if (notifyUser)
            {
                NotifyVirtualGamepadStopped(item, device);
            }

            if (item.ConnectedDevice is not null)
            {
                _autoStartSuppressed.Add(item.Record.ControllerId);
            }
            _ = ApplyLedStateAsync(item);
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            if (updateStatus)
            {
                item.SetStatus($"No se pudo detener su salida virtual: {exception.Message}");
                _reportError?.Invoke(item.StatusMessage, exception);
            }
        }
        finally
        {
            item.SetStarting(false);
        }
    }

    private static IReadOnlyCollection<string> GetComboTargetButtons(ComboEvaluation evaluation)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (ControllerCombo combo in evaluation.TriggeredCombos)
        {
            if (combo.ActionType != ComboActionTypes.Buttons)
            {
                continue;
            }

            foreach (string target in combo.TargetButtons)
            {
                if (GamepadControlCatalog.Buttons.Any(button => button.Id == target))
                {
                    targets.Add(target);
                }
            }
        }

        return targets;
    }

    private async Task PollCombosWhileOutputStoppedAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        CancellationToken cancellationToken)
    {
        if (_inputService is null || _comboEvaluator is null)
        {
            return;
        }

        int pollIntervalMs = EffectiveInputPollIntervalMs(item.Record);
        long now = Environment.TickCount64;
        if (_lastVirtualPollReadMs.TryGetValue(device.DeviceKey, out long lastPollMs) &&
            now - lastPollMs < pollIntervalMs)
        {
            return;
        }

        ControllerProfile? profile = FindAssignedProfile(item.Record);
        if (profile?.Combos is not { Count: > 0 })
        {
            return;
        }
        _lastVirtualPollReadMs[device.DeviceKey] = now;
        GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(
            device.DeviceKey,
            cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        ComboEvaluation evaluation = _comboEvaluator.Evaluate(
            device.DeviceKey,
            snapshot,
            profile?.Combos);
        await ApplyComboActionsAsync(
            item,
            device,
            snapshot,
            profile,
            evaluation,
            virtualOutputActive: false);
    }


    private async Task ApplyComboActionsAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? profile,
        ComboEvaluation evaluation,
        bool virtualOutputActive = true)
    {
        if (evaluation.TriggeredCombos.Count == 0)
        {
            return;
        }

        foreach (ControllerCombo combo in evaluation.TriggeredCombos)
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Combo disparado. Controlador={item.Record.ControllerId}; " +
                $"nombre=\"{combo.DisplayName}\"; accion={combo.ActionType}; " +
                $"botones={string.Join("+", combo.Buttons)}; mando={DescribeDevice(device)}.");
            if (!virtualOutputActive &&
                combo.ActionType != ComboActionTypes.PowerOff &&
                combo.ActionType != ComboActionTypes.StopOutput &&
                combo.ActionType != ComboActionTypes.SwitchType &&
                combo.ActionType != ComboActionTypes.ShowInfo)
            {
                continue;
            }

            if (combo.ActionType == ComboActionTypes.StopOutput)
            {
                if (virtualOutputActive)
                {
                    item.SetStatus($"Combo «{combo.DisplayName}»: se detuvo la salida virtual.");
                    await StopVirtualGamepadNotifyingAsync(item);
                }
                else
                {
                    item.SetStatus($"Combo «{combo.DisplayName}»: se reactivó la salida virtual.");
                    await StartVirtualGamepadAsync(item);
                }

                return;
            }

            if (combo.ActionType == ComboActionTypes.SwitchType)
            {
                if (virtualOutputActive)
                {
                    await SwitchVirtualGamepadTypeAsync(item, device, snapshot, profile, combo);
                }
                else
                {
                    await SwitchVirtualGamepadTypeForNextStartAsync(item, device, combo);
                }
                return;
            }

            if (combo.ActionType == ComboActionTypes.PowerOff)
            {
                await PowerOffControllerCoreAsync(item, device, $"combo «{combo.DisplayName}»");
                return;
            }

            if (combo.ActionType == ComboActionTypes.ShowInfo)
            {
                ShowControllerInfoNotification(item, device, profile, combo);
                return;
            }
        }
    }

    /// <summary>
    /// Acción de combo «Mostrar información del mando»: notificación con los
    /// datos del dispositivo físico (modelo, fabricante, serie, conexión,
    /// batería, perfil y tipo de mando virtual configurado).
    /// </summary>
    private void ShowControllerInfoNotification(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        ControllerProfile? profile,
        ControllerCombo combo)
    {
        var lines = new List<string>
        {
            $"Mando: {device.Name}",
            $"Fabricante: {device.ManufacturerLabel}",
            $"Hardware: {device.HardwareIdLabel}"
        };

        if (!string.IsNullOrWhiteSpace(device.SerialNumber) &&
            !string.Equals(device.SerialNumber, "(n/d)", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Serie: {device.SerialNumber}");
        }

        if (device.ConnectionTransportText is { } transport)
        {
            lines.Add($"Conexión: {transport}");
        }

        if (device.HasBatteryInfo)
        {
            lines.Add(device.BatteryInfoText);
        }

        lines.Add($"Perfil: {profile?.DisplayName ?? "Perfil por mando"}");
        lines.Add(
            $"Tipo virtual: {VirtualGamepadTypes.DisplayName(ResolveVirtualGamepadType(item.Record))}");
        if (device.IsHiddenByGdm)
        {
            lines.Add("Estado: oculto por GDM");
        }

        _notifyEvent?.Invoke(device.Name, [.. lines]);
        item.SetStatus($"Combo «{combo.DisplayName}»: información del mando mostrada.");
        WriteAutoHideDiagnostic(
            DiagnosticLogLevel.Information,
            $"Combo de información disparado. Controlador={item.Record.ControllerId}; " +
            $"nombre=\"{combo.DisplayName}\"; mando={DescribeDevice(device)}.");
    }

    private async Task SwitchVirtualGamepadTypeAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? profile,
        ControllerCombo combo)
    {
        IVirtualGamepadService? service = _virtualGamepadService;
        string? currentType = service?.GetRunningVirtualGamepadType(device.DeviceKey);
        if (service is null || currentType is null)
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Warning,
                $"Combo de cambio de tipo sin salida activa que conmutar. " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            return;
        }

        string newType = VirtualGamepadTypes.Other(currentType);
        WriteAutoHideDiagnostic(
            DiagnosticLogLevel.Information,
            $"Cambio de tipo por combo. Controlador={item.Record.ControllerId}; " +
            $"de={currentType} a={newType}; mando={DescribeDevice(device)}.");
        item.SetStarting(true);
        try
        {
            await service.StopAsync(device.DeviceKey);
            await service.StartAsync(
                device,
                snapshot,
                profile?.WithMappingsFor(newType),
                newType);
            item.SetStatus(
                $"Combo «{combo.DisplayName}»: mando virtual {VirtualGamepadTypes.DisplayName(newType)} activo.");
            item.Update(
                item.Record,
                device,
                profile?.DisplayName ?? "Perfil por mando",
                isRunning: true,
                newType);
            NotifyVirtualOutputStateChanged();
            _notifyEvent?.Invoke(
                "Tipo de mando virtual cambiado",
                [$"{device.Name}: {VirtualGamepadTypes.DisplayName(currentType)} → {VirtualGamepadTypes.DisplayName(newType)}",
                 $"Perfil: {profile?.DisplayName ?? "Perfil por mando"}"]);
            _ = ApplyLedStateAsync(item);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException or
            InvalidOperationException or
            IOException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            item.SetStatus(
                $"Combo «{combo.DisplayName}»: no se pudo cambiar el tipo de mando virtual: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
            try
            {
                await service.StartAsync(
                    device,
                    snapshot,
                    profile?.WithMappingsFor(currentType),
                    currentType);
                item.Update(
                    item.Record,
                    device,
                    profile?.DisplayName ?? "Perfil por mando",
                    isRunning: true,
                    currentType);
                NotifyVirtualOutputStateChanged();
                _ = ApplyLedStateAsync(item);
            }
            catch (Exception restartException) when (
                restartException is InvalidOperationException or
                IOException or
                System.ComponentModel.Win32Exception or
                UnauthorizedAccessException)
            {
                item.SetStatus(
                    $"Combo «{combo.DisplayName}»: la salida virtual no pudo reiniciarse: {restartException.Message}");
            }
        }
        finally
        {
            item.SetStarting(false);
        }
    }

    private async Task SwitchVirtualGamepadTypeForNextStartAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        ControllerCombo combo)
    {
        string currentType = ResolveVirtualGamepadType(item.Record);
        string newType = VirtualGamepadTypes.Other(currentType);
        ControllerRecord updated = item.Record with
        {
            SettingOverrides = item.Record.SettingOverrides with
            {
                VirtualGamepadType = newType
            },
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (await SaveRecordAsync(item, updated))
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Cambio de tipo por combo (salida detenida). Controlador={item.Record.ControllerId}; " +
                $"de={currentType} a={newType}; mando={DescribeDevice(device)}.");
            item.SetStatus(
                $"Combo «{combo.DisplayName}»: el próximo mando virtual será {VirtualGamepadTypes.DisplayName(newType)}.");
            item.Update(
                item.Record,
                device,
                item.ProfileName,
                isRunning: false,
                newType);
            NotifyVirtualOutputStateChanged();
            _notifyEvent?.Invoke(
                "Tipo de mando virtual (próximo inicio)",
                [$"{device.Name}: {VirtualGamepadTypes.DisplayName(currentType)} → {VirtualGamepadTypes.DisplayName(newType)}",
                 $"Perfil: {item.ProfileName}"]);
        }
    }

    private async Task<bool> SaveRecordAsync(
        VirtualGamepadControllerViewModel item,
        ControllerRecord updated)
    {
        if (_temporaryControllerIds.Contains(updated.ControllerId))
        {
            int tempIndex = _temporaryRecords.FindIndex(
                record => record.ControllerId == updated.ControllerId);
            if (tempIndex >= 0)
            {
                _temporaryRecords[tempIndex] = updated;
            }

            item.UpdateRecord(updated);
            return true;
        }

        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar la configuración.");
            return false;
        }

        await _controllerRepository.SaveAsync(updated);
        int index = SavedControllers.IndexOf(item.Record);
        if (index >= 0)
        {
            SavedControllers[index] = updated;
        }

        item.UpdateRecord(updated);
        _onControllerUpdated(updated);
        return true;
    }

    private async Task ToggleAutoStartVirtualGamepadAsync(VirtualGamepadControllerViewModel item)
    {
        if (_controllerRepository is null || item.IsStarting)
        {
            item.SetStatus("No se puede guardar la opción de inicio automático.");
            return;
        }

        bool startNow = false;
        item.SetStarting(true);
        try
        {
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    AutoStartVirtualGamepad =
                        !EffectiveSettings(item.Record).AutoStartVirtualGamepad
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            item.SetStatus(EffectiveSettings(updated).AutoStartVirtualGamepad
                ? "Se iniciará automáticamente cuando conectes este mando."
                : "El inicio automático quedó desactivado.");
            startNow = EffectiveSettings(updated).AutoStartVirtualGamepad &&
                item.ConnectedDevice is not null;
            if (startNow && item.ConnectedDevice is { } device)
            {
                _autoStartAttempts[updated.ControllerId] = device.DeviceKey;
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar la preferencia: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
        finally
        {
            item.SetStarting(false);
        }

        if (startNow)
        {
            await StartVirtualGamepadAsync(item);
        }
    }

    private async Task ToggleAutoHidePhysicalGamepadAsync(VirtualGamepadControllerViewModel item)
    {
        if (_controllerRepository is null || item.IsStarting)
        {
            item.SetStatus("No se puede guardar la opción de ocultamiento automático.");
            return;
        }

        item.SetStarting(true);
        try
        {
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    AutoHidePhysicalGamepad =
                        !EffectiveSettings(item.Record).AutoHidePhysicalGamepad
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            item.SetStatus(EffectiveSettings(updated).AutoHidePhysicalGamepad
                ? "El mando físico se ocultará al iniciar la salida virtual."
                : "El ocultamiento automático del mando físico quedó desactivado.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar la preferencia: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
        finally
        {
            item.SetStarting(false);
        }
    }

    private bool IsDeviceHiddenByGdm(ConnectedGamepad device)
    {
        if (string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return false;
        }

        if (_isDeviceHiddenByGdm is not null)
        {
            return _isDeviceHiddenByGdm(device);
        }

        return device.IsHiddenByGdm;
    }
    private void RefreshPowerOffSupportIfNeeded(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad? device)
    {
        if (_powerOffService is null)
        {
            return;
        }

        string key = device?.DeviceKey ?? "(desconectado)";
        if (_powerOffSupportCheckedKeys.GetValueOrDefault(item.Record.ControllerId) == key)
        {
            return;
        }

        _powerOffSupportCheckedKeys[item.Record.ControllerId] = key;
        if (device is null)
        {
            item.SetPowerOffSupport("Conecta el mando físico para consultar si se puede apagar.");
            return;
        }

        if (device.IsVirtualGamepad || string.IsNullOrWhiteSpace(device.DevicePath))
        {
            item.SetPowerOffSupport("Los mandos virtuales no se pueden apagar desde GDM.");
            return;
        }

        _ = RefreshPowerOffSupportAsync(item, device);
    }

    private async Task RefreshPowerOffSupportAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device)
    {
        try
        {
            bool supported = await _powerOffService!.SupportsPowerOffAsync(device);
            if (item.ConnectedDevice?.DeviceKey != device.DeviceKey)
            {
                return;
            }

            item.SetPowerOffSupport(supported
                ? "Apagado disponible: el mando está conectado por Bluetooth. Al apagarlo se suspenderá sin desemparejarlo de Windows; pulsa su botón de encendido para reconectarlo."
                : "Este mando no se puede apagar por software: el apagado solo está disponible para mandos conectados por Bluetooth.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            item.SetPowerOffSupport("No se pudo consultar el soporte de apagado del mando.");
        }
    }

    public Task PowerOffControllerAsync(VirtualGamepadControllerViewModel item)
    {
        if (item.ConnectedDevice is not { } device)
        {
            item.SetStatus("Conecta el mando físico para poder apagarlo.");
            return Task.CompletedTask;
        }

        return PowerOffControllerCoreAsync(item, device, "manual");
    }

    private async Task PowerOffControllerCoreAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        string reason)
    {
        IControllerPowerOffService? service = _powerOffService;
        if (service is null)
        {
            item.SetStatus("El apagado de mandos no está disponible en esta sesión.");
            return;
        }

        Task? stopTask = _virtualGamepadService?.IsRunningFor(device.DeviceKey) == true
            ? StopVirtualGamepadSilentlyAsync(item)
            : null;

        item.SetStatus($"Apagando el mando físico ({reason})…");
        var powerOffWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ControllerPowerOffResult result = await service.TryPowerOffAsync(device);
            powerOffWatch.Stop();

            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Apagado de mando físico completado. Controlador={item.Record.ControllerId}; " +
                $"motivo={reason}; ioctlMs={powerOffWatch.ElapsedMilliseconds}; " +
                $"estado={result.Status}; mando={DescribeDevice(device)}.");
            switch (result.Status)
            {
                case ControllerPowerOffStatus.PoweredOff:
                    item.SetStatus(
                        $"Mando apagado ({reason}). Pulsa su botón de encendido para reconectarlo; no se ha desemparejado de Windows.");
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Information,
                        $"Mando físico apagado por {reason}. Controlador={item.Record.ControllerId}; " +
                        $"mando={DescribeDevice(device)}.");
                    break;
                case ControllerPowerOffStatus.NotConnected:
                    item.SetStatus(
                        $"No se pudo apagar el mando ({reason}): {result.Detail}. Si está conectado por USB no admite apagado por software.");
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Warning,
                        $"Apagado de mando sin enlace Bluetooth activo. Controlador={item.Record.ControllerId}; " +
                        $"mando={DescribeDevice(device)}; motivo={reason}.");
                    break;
                case ControllerPowerOffStatus.UnsupportedDevice:
                    item.SetStatus(
                        $"No se puede apagar este mando: {result.Detail ?? "solo los mandos Bluetooth admiten apagado"}.");
                    break;
                default:
                    item.SetStatus($"No se pudo apagar el mando ({reason}): {result.Detail ?? "error desconocido"}.");
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Warning,
                        $"Falló el apagado del mando físico. Controlador={item.Record.ControllerId}; " +
                        $"mando={DescribeDevice(device)}; motivo={reason}; detalle={result.Detail}.");
                    break;
            }

            if (stopTask is not null)
            {
                await stopTask;
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            if (stopTask is not null)
            {
                try
                {
                    await stopTask;
                }
                catch
                {
                }
            }

            item.SetStatus($"No se pudo apagar el mando ({reason}): {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task SaveVibrationSettingsAsync(
        VirtualGamepadControllerViewModel item,
        bool enabled)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar la vibración.");
            return;
        }

        try
        {
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    VibrationEnabled = enabled
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar la vibración: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task SaveInputPollIntervalSettingsAsync(
        VirtualGamepadControllerViewModel item,
        int intervalMs)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar el intervalo de sondeo.");
            return;
        }

        try
        {
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    InputPollIntervalMs = intervalMs <= 0 ? 0 : Math.Clamp(intervalMs, 4, 100)
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            VirtualPollIntervalChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar el intervalo de sondeo: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task SaveMotionSettingsAsync(
        VirtualGamepadControllerViewModel item,
        bool enabled)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar el ajuste de movimiento.");
            return;
        }

        try
        {
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    MotionEnabled = enabled
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar el movimiento: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task SaveMotionIntervalSettingsAsync(
        VirtualGamepadControllerViewModel item,
        int intervalMs)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar el intervalo de movimiento.");
            return;
        }

        try
        {
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    MotionPollIntervalMs = intervalMs <= 0 ? 0 : Math.Clamp(intervalMs, 4, 100)
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar el intervalo de movimiento: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task SaveMotionInvertSettingsAsync(
        VirtualGamepadControllerViewModel item,
        bool invertPitch,
        bool invertYaw,
        bool invertRoll)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar la inversión de ejes.");
            return;
        }

        try
        {
            ControllerRecord updated = item.Record with
            {
                Settings = item.Record.Settings with
                {
                    MotionInvertGyroPitch = invertPitch,
                    MotionInvertGyroYaw = invertYaw,
                    MotionInvertGyroRoll = invertRoll
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar la inversión de ejes: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task CalibrateMotionAsync(VirtualGamepadControllerViewModel item)
    {
        if (_inputService is null || _controllerRepository is null)
        {
            item.SetStatus("No se puede calibrar: faltan servicios.");
            return;
        }

        ConnectedGamepad? device = item.ConnectedDevice;
        if (device is null)
        {
            item.SetStatus("Conecta el mando físico antes de calibrar.");
            return;
        }

        item.SetStatus("Calibrando movimiento: mantén el mando quieto...");
        double pitch = 0d, yaw = 0d, roll = 0d;
        int samples = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.ElapsedMilliseconds < 1500)
        {
            try
            {
                GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(device.DeviceKey);
                if (snapshot?.Motion is { HasGyroscope: true } motion)
                {
                    pitch += motion.GyroPitchRadS;
                    yaw += motion.GyroYawRadS;
                    roll += motion.GyroRollRadS;
                    samples++;
                }
            }
            catch
            {
            }

            await Task.Delay(50);
        }

        if (samples == 0)
        {
            item.SetStatus("No se recibieron datos del giróscopo; no se calibró.");
            return;
        }

        try
        {
            ControllerRecord updated = item.Record with
            {
                Settings = item.Record.Settings with
                {
                    MotionCalibrated = true,
                    MotionGyroBiasPitchRadS = pitch / samples,
                    MotionGyroBiasYawRadS = yaw / samples,
                    MotionGyroBiasRollRadS = roll / samples
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            item.SetStatus($"Movimiento calibrado con {samples} muestras.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar la calibración: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    private void OnVirtualRumbleOutput(string deviceKey, int lowMotor, int highMotor)
    {
        if (_inputService is null)
        {
            return;
        }

        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            VirtualGamepadControllerViewModel? item = VirtualGamepads.FirstOrDefault(
                candidate => candidate.ConnectedDevice?.DeviceKey == deviceKey);
            if (item is null || !EffectiveSettings(item.Record).VibrationEnabled)
            {
                _rumbleHoldStates.Remove(deviceKey);
                return;
            }

            int low = Math.Clamp(lowMotor, 0, 255) * 257;
            int high = Math.Clamp(highMotor, 0, 255) * 257;
            long now = Environment.TickCount64;
            if (low == 0 && high == 0)
            {
                _rumbleHoldStates.Remove(deviceKey);
                _ = _inputService.RumbleAsync(deviceKey, 0, 0, 60);
                return;
            }

            _rumbleHoldStates[deviceKey] = new RumbleHoldState(low, high, now);
            EnsureRumbleHoldTimer();
            if (now - _lastRumbleForwardLogMs > 2000)
            {
                _lastRumbleForwardLogMs = now;
                _writeDiagnostic?.Invoke(
                    DiagnosticLogLevel.Information,
                    "Reenvío de vibración",
                    null);
            }
            _ = _inputService.RumbleAsync(deviceKey, low, high, 200);
        });
    }

    private void EnsureRumbleHoldTimer()
    {
        if (_rumbleHoldTimer is not null)
        {
            return;
        }

        _rumbleHoldTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _rumbleHoldTimer.Tick += (_, _) => PulseRumbleHolds();
        _rumbleHoldTimer.Start();
    }

    private void PulseRumbleHolds()
    {
        if (_inputService is null)
        {
            _rumbleHoldStates.Clear();
            return;
        }

        long now = Environment.TickCount64;
        foreach ((string deviceKey, RumbleHoldState state) in _rumbleHoldStates.ToArray())
        {
            VirtualGamepadControllerViewModel? item = VirtualGamepads.FirstOrDefault(
                candidate => candidate.ConnectedDevice?.DeviceKey == deviceKey);
            if (item is null ||
                !EffectiveSettings(item.Record).VibrationEnabled ||
                now - state.LastEventMs > 400)
            {
                _rumbleHoldStates.Remove(deviceKey);
                continue;
            }

            _ = _inputService.RumbleAsync(deviceKey, state.Low, state.High, 160);
        }

        if (_rumbleHoldStates.Count == 0)
        {
            _rumbleHoldTimer?.Stop();
            _rumbleHoldTimer = null;
        }
    }

    public async Task PollControllerIdlePowerOffAsync()
    {
        if (_inputService is null || _powerOffService is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads.ToArray())
        {
            int timeoutSeconds = ControllerPowerOff.Normalize(EffectiveSettings(item.Record).PowerOffIdleSeconds);
            ConnectedGamepad? device = item.ConnectedDevice;
            if (timeoutSeconds <= 0 || device is null || device.IsVirtualGamepad)
            {
                _idlePowerOffStates.Remove(item.Record.ControllerId);
                continue;
            }

            if (!_idlePowerOffStates.TryGetValue(item.Record.ControllerId, out IdlePowerOffState? state) ||
                state.DeviceKey != device.DeviceKey)
            {
                _idlePowerOffStates[item.Record.ControllerId] = new IdlePowerOffState(
                    device.DeviceKey,
                    lastSnapshot: null,
                    now,
                    powerOffTriggered: false);
                continue;
            }

            GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(device.DeviceKey);
            if (snapshot is null)
            {
                _idlePowerOffStates.Remove(item.Record.ControllerId);
                continue;
            }

            if (state.LastSnapshot is null || !SnapshotsEqual(state.LastSnapshot, snapshot))
            {
                state.LastSnapshot = snapshot;
                state.LastActivityUtc = now;
                state.PowerOffTriggered = false;
                continue;
            }

            if (!state.PowerOffTriggered &&
                now - state.LastActivityUtc >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                state.PowerOffTriggered = true;
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Apagado por inactividad. Controlador={item.Record.ControllerId}; " +
                    $"mando={DescribeDevice(device)}; tiempoSinActividad={(now - state.LastActivityUtc).TotalSeconds:F0}s; " +
                    $"salidaVirtualActiva={_virtualGamepadService?.IsRunningFor(device.DeviceKey) == true}.");
                await PowerOffControllerCoreAsync(
                    item,
                    device,
                    $"inactividad de {FormatIdleTimeout(timeoutSeconds)}");
            }
        }
    }

    private static bool SnapshotsEqual(GamepadInputSnapshot first, GamepadInputSnapshot second)
    {
        if (first.Buttons.Count != second.Buttons.Count ||
            first.Axes.Count != second.Axes.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Buttons.Count; index++)
        {
            if (first.Buttons[index].IsPressed != second.Buttons[index].IsPressed)
            {
                return false;
            }
        }

        for (int index = 0; index < first.Axes.Count; index++)
        {
            if (Math.Abs(first.Axes[index].Value - second.Axes[index].Value) > 0.001)
            {
                return false;
            }
        }

        return TouchpadsEqual(first.Touchpad, second.Touchpad);
    }

    private static bool TouchpadsEqual(TouchpadState? first, TouchpadState? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return TouchpadFingersEqual(first.Finger0, second.Finger0) &&
            TouchpadFingersEqual(first.Finger1, second.Finger1);
    }

    private static bool TouchpadFingersEqual(
        TouchpadFingerState? first,
        TouchpadFingerState? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return first.Active == second.Active &&
            first.Id == second.Id &&
            (!first.Active ||
                (Math.Abs(first.X - second.X) <= 0.02 &&
                 Math.Abs(first.Y - second.Y) <= 0.02));
    }

    private static string FormatIdleTimeout(int seconds) =>
        seconds % 3600 == 0
            ? $"{seconds / 3600} h"
            : seconds % 60 == 0
                ? $"{seconds / 60} min"
                : $"{seconds} s";

    private sealed class IdlePowerOffState(
        string deviceKey,
        GamepadInputSnapshot? lastSnapshot,
        DateTimeOffset lastActivityUtc,
        bool powerOffTriggered)
    {
        public string DeviceKey { get; } = deviceKey;
        public GamepadInputSnapshot? LastSnapshot { get; set; } = lastSnapshot;
        public DateTimeOffset LastActivityUtc { get; set; } = lastActivityUtc;
        public bool PowerOffTriggered { get; set; } = powerOffTriggered;
    }

    private async Task SavePowerOffSettingsAsync(
        VirtualGamepadControllerViewModel item,
        int idleSeconds)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar el apagado por inactividad.");
            return;
        }

        try
        {
            int normalized = ControllerPowerOff.Normalize(idleSeconds);
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    PowerOffIdleSeconds = normalized
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            _idlePowerOffStates.Remove(item.Record.ControllerId);
            item.SetStatus(normalized > 0
                ? $"El mando se apagará tras {FormatIdleTimeout(normalized)} sin actividad."
                : "Apagado por inactividad desactivado.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar el apagado por inactividad: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    private async Task SaveLedSettingsAsync(
        VirtualGamepadControllerViewModel item,
        ControllerLedSettings ledSettings)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se pueden guardar las opciones de LED.");
            return;
        }

        try
        {
            ControllerLedSettings normalized = ControllerLedSettings.Normalize(ledSettings);
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = item.Record.SettingOverrides with
                {
                    Led = normalized
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            await ApplyLedStateAsync(item);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudieron guardar las opciones de LED: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    private async Task ApplyLedStateAsync(VirtualGamepadControllerViewModel item)
    {
        ISonyControllerLedService? service = _ledService;
        ConnectedGamepad? device = item.ConnectedDevice;
        if (service is null ||
            device?.DevicePath is null ||
            !SonyControllerLedSupport.TryGetKind(
                device.VendorId,
                device.ProductId,
                out SonyControllerLedKind ledKind))
        {
            return;
        }

        ControllerSettings settings = EffectiveSettings(item.Record);
        ControllerLedSettings led = ControllerLedSettings.Normalize(settings.Led);
        byte red = 0, green = 0, blue = 0;
        int? playerNumber = null;
        switch (led.Mode)
        {
            case ControllerLedModes.Player:
                playerNumber = led.PlayerNumber;
                ControllerLedColor.TryParse(
                    led.PlayerColors.GetValueOrDefault(led.PlayerNumber),
                    out red,
                    out green,
                    out blue);
                break;
            case ControllerLedModes.Manual:
                ControllerLedColor.TryParse(led.ManualColor, out red, out green, out blue);
                break;
            case ControllerLedModes.VirtualOutput when item.IsRunning:
                string? runningType = _virtualGamepadService?.GetRunningVirtualGamepadType(device.DeviceKey);
                bool isXbox = runningType == VirtualGamepadTypes.Xbox360;
                red = 0;
                green = isXbox ? (byte)255 : (byte)0;
                blue = isXbox ? (byte)0 : (byte)255;
                break;
            case ControllerLedModes.VirtualOutput:
                red = 200;
                green = 200;
                blue = 200;
                break;
        }

        byte brightness = (byte)led.Brightness;
        double? effectScale = null;
        if (GetActiveBatteryAlertLevel(device.DeviceKey) is not null)
        {
            if (settings.BatteryAlertColorEnabled &&
                ControllerLedColor.TryParse(settings.BatteryAlertColor, out byte alertRed, out byte alertGreen, out byte alertBlue))
            {
                red = alertRed;
                green = alertGreen;
                blue = alertBlue;
                playerNumber = null;
            }

            if (settings.BatteryAlertEffectEnabled)
            {
                effectScale = BatteryEffectBrightnessScale(settings.BatteryAlertEffect);
            }
        }

        if (effectScale is null && IsChargingOrWired(device))
        {
            string chargingEffect = ChargingLedEffects.Normalize(settings.ChargingLedEffect);
            if (chargingEffect != ChargingLedEffects.None)
            {
                effectScale = ChargingEffectBrightnessScale(chargingEffect);
            }
        }

        if (effectScale is { } scale)
        {
            if (settings.LedEffectColorIntensity ||
                ledKind == SonyControllerLedKind.DualShock4)
            {
                red = ScaleLedChannel(red, scale);
                green = ScaleLedChannel(green, scale);
                blue = ScaleLedChannel(blue, scale);
            }
            else
            {
                brightness = (byte)Math.Clamp(
                    Math.Round(led.Brightness * scale),
                    ControllerLedSettings.MinBrightness,
                    ControllerLedSettings.MaxBrightness);
            }
        }
        var desiredLed = (red, green, blue, brightness, playerNumber);
        if (_lastLedWrites.TryGetValue(device.DeviceKey, out var lastWrite) &&
            lastWrite == desiredLed)
        {
            return;
        }

        try
        {
            SonyLedApplyResult result = await service.TrySetLightBarAsync(
                device,
                red,
                green,
                blue,
                brightness,
                playerNumber);
            if (result != SonyLedApplyResult.Failed)
            {
                _lastLedWrites[device.DeviceKey] = desiredLed;
            }

            if (_lastLedResults.GetValueOrDefault(device.DeviceKey) != result)
            {
                _lastLedResults[device.DeviceKey] = result;
                if (result == SonyLedApplyResult.Failed)
                {
                    WriteAutoHideDiagnostic(
                        DiagnosticLogLevel.Warning,
                        $"No se pudo aplicar el color del LED. Controlador={item.Record.ControllerId}; " +
                        $"mando={DescribeDevice(device)}; detalle={service.LastFailureDetail ?? "sin detalle"}.");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            InvalidOperationException)
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Warning,
                $"Error al aplicar el LED del mando. Controlador={item.Record.ControllerId}; " +
                $"mando={DescribeDevice(device)}; error={exception.Message}");
        }
    }

    public IReadOnlyDictionary<string, bool> GetAutoHiddenDeviceExitPolicies()
    {
        return VirtualGamepads
            .Where(item =>
                EffectiveSettings(item.Record).AutoHidePhysicalGamepad &&
                item.ConnectedDevice is { DevicePath: not null } device &&
                _autoHiddenDeviceKeys.Contains(device.DeviceKey))
            .ToDictionary(
                item => item.ConnectedDevice!.DevicePath!,
                item => EffectiveSettings(item.Record).KeepHiddenOnExit,
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> RestoreAutoHiddenDeviceAsync(ConnectedGamepad device, bool force = false)
    {
        if (!_autoHiddenDeviceKeys.Contains(device.DeviceKey))
        {
            return true;
        }

        if (!force)
        {
            VirtualGamepadControllerViewModel? owner = VirtualGamepads.FirstOrDefault(
                candidate => candidate.ConnectedDevice?.DeviceKey == device.DeviceKey);
            if (owner is null ||
                EffectiveSettings(owner.Record).AutoHidePhysicalGamepad != true)
            {
                WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"El mando oculto se mantiene oculto porque el ocultamiento automático está desactivado. " +
                $"mando={DescribeDevice(device)}.");
                return true;
            }
        }

        if (_showPhysicalDevice is null)
        {
            return false;
        }

        if (await _showPhysicalDevice(device))
        {
            _autoHiddenDeviceKeys.Remove(device.DeviceKey);
            _autoHiddenPresenceStates.Remove(device.DeviceKey);
            _hiddenInputUnavailableStates.Remove(device.DeviceKey);
            return true;
        }

        return false;
    }

    private async Task<bool> ConfirmHiddenInputUnavailableAsync(ConnectedGamepad device)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DevicePresenceState state =
            _hiddenInputUnavailableStates.GetValueOrDefault(device.DeviceKey);
        if (state.LastCheckedAtUtc != default &&
            now - state.LastCheckedAtUtc < TimeSpan.FromMilliseconds(250))
        {
            return false;
        }

        int missingChecks = state.MissingChecks + 1;
        _hiddenInputUnavailableStates[device.DeviceKey] =
            new DevicePresenceState(now, missingChecks);
        WriteAutoHideDiagnostic(
            DiagnosticLogLevel.Warning,
            $"SDL devolvió entrada nula para mando oculto: comprobación {missingChecks}/15; " +
            $"mando={DescribeDevice(device)}; salidaVirtualActiva={_virtualGamepadService?.IsRunningFor(device.DeviceKey)}.");
        return missingChecks >= 15;
    }

    private async Task<bool> CheckPhysicalPresenceAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device)
    {
        if (_isPhysicalDevicePresent is null ||
            string.IsNullOrWhiteSpace(device.DevicePath) ||
            device.IsVirtualGamepad)
        {
            return false;
        }

        long now = Environment.TickCount64;
        if (_physicalPresenceCheckMs.TryGetValue(device.DeviceKey, out long lastCheck) &&
            now - lastCheck < PhysicalPresenceCheckIntervalMs)
        {
            return _phantomDeviceKeys.Contains(device.DeviceKey);
        }
        _physicalPresenceCheckMs[device.DeviceKey] = now;

        bool present;
        try
        {
            present = _isPhysicalDevicePresent(device);
        }
        catch
        {
            return false;
        }

        if (present)
        {
            _physicalPresenceMissingSinceMs.Remove(device.DeviceKey);
            _lastPhysicalRescanMs.Remove(device.DeviceKey);
            _phantomDeviceKeys.Remove(device.DeviceKey);
            return false;
        }

        if (!_physicalPresenceMissingSinceMs.TryGetValue(device.DeviceKey, out long missingSince))
        {
            _physicalPresenceMissingSinceMs[device.DeviceKey] = now;
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Warning,
                $"Windows ya no ve el dispositivo físico aunque SDL lo sigue reportando; se forzará reenumeración. mando={DescribeDevice(device)}.");
            return false;
        }

        if (now - missingSince >= PhysicalPresenceDefinitiveMissingMs)
        {
            if (_phantomDeviceKeys.Add(device.DeviceKey))
            {
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Warning,
                    $"Windows sigue sin ver el dispositivo físico; se tratará como desconectado aunque SDL lo reporte. mando={DescribeDevice(device)}.");
                item.SetStatus(
                    "Windows ya no detecta el mando físico; se tratará como desconectado.");
            }

            return true;
        }

        if (now - missingSince < PhysicalPresenceMissingGraceMs ||
            (_lastPhysicalRescanMs.TryGetValue(device.DeviceKey, out long lastRescan) &&
             now - lastRescan < PhysicalPresenceRescanIntervalMs))
        {
            return false;
        }

        if (_inputService is not IGamepadDetectionService detection)
        {
            return false;
        }

        _lastPhysicalRescanMs[device.DeviceKey] = now;
        try
        {
            await detection.EnumerateConnectedAsync();
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Warning,
                $"Se forzó la reenumeración SDL porque Windows no ve el dispositivo físico. mando={DescribeDevice(device)}.");
        }
        catch (Exception exception)
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Error,
                $"Falló la reenumeración forzada de dispositivos. mando={DescribeDevice(device)}.",
                exception);
        }

        return false;
    }

    private bool IsPhysicallyPresent(ConnectedGamepad device)
    {
        if (_isPhysicalDevicePresent is null ||
            string.IsNullOrWhiteSpace(device.DevicePath) ||
            device.IsVirtualGamepad)
        {
            return true;
        }

        try
        {
            return _isPhysicalDevicePresent(device);
        }
        catch
        {
            return true;
        }
    }

    private async Task<bool> CheckVirtualDevicePresenceAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? profile,
        string? runningType,
        CancellationToken cancellationToken)
    {
        if (_virtualGamepadService is null ||
            !VirtualGamepadTypes.IsSupported(runningType))
        {
            _virtualDeviceMissingSinceMs.Remove(device.DeviceKey);
            return false;
        }

        if (HasVirtualDeviceForType(runningType!))
        {
            _virtualDeviceMissingSinceMs.Remove(device.DeviceKey);
            return false;
        }

        long now = Environment.TickCount64;
        if (!_virtualDeviceMissingSinceMs.TryGetValue(device.DeviceKey, out long missingSince))
        {
            _virtualDeviceMissingSinceMs[device.DeviceKey] = now;
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Warning,
                $"El mando virtual {runningType} no aparece en el sistema; se reiniciará la salida si no vuelve. " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            return false;
        }

        if (now - missingSince < VirtualDeviceMissingGraceMs)
        {
            return false;
        }

        _virtualDeviceMissingSinceMs.Remove(device.DeviceKey);
        await RestartVanishedVirtualGamepadAsync(
            item, device, snapshot, profile, runningType!, cancellationToken);
        return true;
    }

    private bool HasVirtualDeviceForType(string type)
    {
        if (_allConnectedDevices.Length == 0 ||
            _virtualGamepadService is null)
        {
            return true;
        }

        (ushort vendorId, ushort productId) = VirtualIdentityForType(type);
        int present = _allConnectedDevices.Count(connected =>
            connected.IsVirtualGamepad &&
            connected.VendorId == vendorId &&
            connected.ProductId == productId);
        int expected = VirtualGamepads.Count(entry =>
        {
            string? key = entry.ConnectedDevice?.DeviceKey;
            return key is not null &&
                _virtualGamepadService.IsRunningFor(key) &&
                string.Equals(
                    _virtualGamepadService.GetRunningVirtualGamepadType(key),
                    type,
                    StringComparison.Ordinal);
        });
        return present >= Math.Max(1, expected);
    }

    private async Task RestartVanishedVirtualGamepadAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? profile,
        string virtualType,
        CancellationToken cancellationToken)
    {
        IVirtualGamepadService? service = _virtualGamepadService;
        if (service is null)
        {
            return;
        }

        WriteAutoHideDiagnostic(
            DiagnosticLogLevel.Warning,
            $"Reinicio de salida virtual por desaparición del dispositivo. " +
            $"Controlador={item.Record.ControllerId}; tipo={virtualType}; mando={DescribeDevice(device)}.");
        item.SetStarting(true);
        try
        {
            await service.StopAsync(device.DeviceKey, cancellationToken);
            _comboEvaluator.Reset(device.DeviceKey);
            await service.StartAsync(
                device,
                snapshot,
                profile?.WithMappingsFor(virtualType),
                virtualType,
                cancellationToken);
            item.SetStatus(
                $"Se reinició el mando virtual {VirtualGamepadTypes.DisplayName(virtualType)} porque desapareció del sistema.");
            item.Update(
                item.Record,
                device,
                profile?.DisplayName ?? "Perfil por mando",
                isRunning: true,
                virtualType);
            NotifyVirtualOutputStateChanged();
            _ = ApplyLedStateAsync(item);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException or
            InvalidOperationException or
            IOException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo reiniciar el mando virtual: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
        finally
        {
            item.SetStarting(false);
        }
    }

    private static (ushort VendorId, ushort ProductId) VirtualIdentityForType(string type) =>
        string.Equals(type, VirtualGamepadTypes.DualShock4, StringComparison.Ordinal)
            ? ((ushort)0x054C, (ushort)0x09CC)
            : ((ushort)0x045E, (ushort)0x028E);
    private async Task UpdateBatteryAlertAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        CancellationToken cancellationToken)
    {
        ControllerSettings settings = EffectiveSettings(item.Record);
        GamepadBatteryLevel level = device.BatteryLevel;
        _lastBatteryLevels[device.DeviceKey] = level;
        item.SetBatteryInfo(device.BatteryInfoText);

        if (!settings.BatteryAlertEnabled || _inputService is null)
        {
            EvaluateBatteryAlert(item, device, level);
            return;
        }

        long now = Environment.TickCount64;
        if (_lastBatteryReadMs.TryGetValue(device.DeviceKey, out long lastRead) &&
            now - lastRead < BatteryReadIntervalMs)
        {
            EvaluateBatteryAlert(item, device, level);
            return;
        }

        _lastBatteryReadMs[device.DeviceKey] = now;
        try
        {
            GamepadInputSnapshot? snapshot = await _inputService.ReadInputAsync(
                device.DeviceKey,
                cancellationToken);
            if (snapshot is not null)
            {
                level = snapshot.BatteryLevel;
                _lastBatteryLevels[device.DeviceKey] = level;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
        }

        EvaluateBatteryAlert(item, device, level);
    }

    private void EvaluateBatteryAlert(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadBatteryLevel level)
    {
        ControllerSettings settings = EffectiveSettings(item.Record);
        _lastBatteryLevels[device.DeviceKey] = level;
        item.SetBatteryInfo((device with { BatteryLevel = level }).BatteryInfoText);
        int threshold = BatteryAlertLevels.Normalize(settings.BatteryAlertLevel);
        bool active = settings.BatteryAlertEnabled &&
            level >= GamepadBatteryLevel.Empty &&
            (int)level <= threshold;
        string key = device.DeviceKey;
        bool wasActive = _batteryAlertActive.TryGetValue(key, out bool previous) && previous;
        if (active == wasActive)
        {
            if (active)
            {
                NotifyBatteryAlertRepeat(item, device, level);
            }

            return;
        }

        _batteryAlertActive[key] = active;
        if (active)
        {
            _batteryAlertNotifyMs[key] = Environment.TickCount64;
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Alerta de batería activada ({ConnectedGamepad.BatteryLevelDisplayName(level)}). " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            ApplyBatteryNotification(item, device, level);
        }
        else
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Alerta de batería desactivada ({ConnectedGamepad.BatteryLevelDisplayName(level)}). " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
        }

        _ = ApplyLedStateAsync(item);
        EnsureBatteryEffectTimer();
            EnsureLedGuardTimer();
    }

    private void NotifyBatteryAlertRepeat(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadBatteryLevel level)
    {
        ControllerSettings settings = EffectiveSettings(item.Record);
        long now = Environment.TickCount64;
        if (_batteryAlertNotifyMs.TryGetValue(device.DeviceKey, out long last) &&
            now - last < BatteryNotifyRepeatMs)
        {
            return;
        }

        _batteryAlertNotifyMs[device.DeviceKey] = now;
        ApplyBatteryNotification(item, device, level);
    }

    private void ApplyBatteryNotification(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadBatteryLevel level)
    {
        ControllerSettings settings = EffectiveSettings(item.Record);
        if (settings.BatteryNotifyEnabled)
        {
            item.SetStatus(
                $"Batería {ConnectedGamepad.BatteryLevelDisplayName(level).ToLowerInvariant()}: conecta el cargador.");
            _notifyEvent?.Invoke(
                $"Baja batería · {device.Name}",
                [device.BatteryInfoText, "Conecta el cargador para evitar que se apague."]);
        }

        if (settings.BatterySoundEnabled)
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch
            {
            }
        }
    }

    private GamepadBatteryLevel? GetActiveBatteryAlertLevel(string deviceKey) =>
        _batteryAlertActive.TryGetValue(deviceKey, out bool active) && active
            ? _lastBatteryLevels.GetValueOrDefault(deviceKey, GamepadBatteryLevel.Unknown)
            : null;

    private void EnsureBatteryEffectTimer()
    {
        bool needed = VirtualGamepads.Any(item =>
        {
            ConnectedGamepad? device = item.ConnectedDevice;
            if (device is null ||
                !SonyControllerLedSupport.TryGetKind(device.VendorId, device.ProductId, out _))
            {
                return false;
            }

            ControllerSettings settings = EffectiveSettings(item.Record);
            if (_batteryAlertActive.GetValueOrDefault(device.DeviceKey) &&
                settings.BatteryAlertEnabled &&
                settings.BatteryAlertEffectEnabled)
            {
                return true;
            }

            return IsChargingOrWired(device) &&
                ChargingLedEffects.Normalize(settings.ChargingLedEffect) != ChargingLedEffects.None;
        });

        if (needed && _batteryEffectTimer is null)
        {
            _batteryEffectTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(90)
            };
            _batteryEffectTimer.Tick += (_, _) =>
            {
                foreach (VirtualGamepadControllerViewModel item in VirtualGamepads.ToArray())
                {
                    ConnectedGamepad? device = item.ConnectedDevice;
                    if (device is null)
                    {
                        continue;
                    }

                    ControllerSettings settings = EffectiveSettings(item.Record);
                    bool alertFx = _batteryAlertActive.GetValueOrDefault(device.DeviceKey) &&
                        settings.BatteryAlertEnabled &&
                        settings.BatteryAlertEffectEnabled;
                    bool chargingFx = IsChargingOrWired(device) &&
                        ChargingLedEffects.Normalize(settings.ChargingLedEffect) != ChargingLedEffects.None;
                    if (alertFx || chargingFx)
                    {
                        _ = ApplyLedStateAsync(item);
                    }
                }
            };
            _batteryEffectTimer.Start();
        }
        else if (!needed && _batteryEffectTimer is not null)
        {
            _batteryEffectTimer.Stop();
            _batteryEffectTimer = null;
        }
    }

    private void EnsureLedGuardTimer()
    {
        bool needed = VirtualGamepads.Any(item =>
        {
            ConnectedGamepad? device = item.ConnectedDevice;
            return item.IsRunning &&
                EffectiveSettings(item.Record).IgnoreExternalColorSignals &&
                device is not null &&
                SonyControllerLedSupport.TryGetKind(device.VendorId, device.ProductId, out _);
        });

        if (needed && _ledGuardTimer is null)
        {
            _ledGuardTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _ledGuardTimer.Tick += (_, _) =>
            {
                foreach (VirtualGamepadControllerViewModel item in VirtualGamepads.ToArray())
                {
                    if (item.IsRunning && EffectiveSettings(item.Record).IgnoreExternalColorSignals)
                    {
                        _ = ApplyLedStateAsync(item);
                    }
                }
            };
            _ledGuardTimer.Start();
        }
        else if (!needed && _ledGuardTimer is not null)
        {
            _ledGuardTimer.Stop();
            _ledGuardTimer = null;
        }
    }
    private static byte ScaleLedChannel(byte value, double scale)
    {
        if (value == 0)
        {
            return 0;
        }

        return (byte)Math.Max(1, Math.Round(value * scale));
    }

    private bool IsChargingOrWired(ConnectedGamepad device)
    {
        if (device.BatteryCharging)
        {
            return true;
        }

        GamepadBatteryLevel level =
            _lastBatteryLevels.TryGetValue(device.DeviceKey, out GamepadBatteryLevel last)
                ? last
                : device.BatteryLevel;
        return level is GamepadBatteryLevel.Charging or GamepadBatteryLevel.Wired;
    }

    private static double BatteryEffectBrightnessScale(string effect)
    {
        long now = Environment.TickCount64;
        if (effect == BatteryAlertEffects.Fade)
        {
            return 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * (now % 2000) / 2000.0);
        }

        return (now % 1000) < 500 ? 1.0 : 0.0;
    }

    private static double ChargingEffectBrightnessScale(string effect)
    {
        long now = Environment.TickCount64;
        if (effect == ChargingLedEffects.Fade)
        {
            const long cycleMs = 5000;
            const long holdMs = 3000;
            long phase = now % cycleMs;
            if (phase < holdMs)
            {
                return 1.0;
            }

            double u = (phase - holdMs) / 2000.0;
            return 0.5 + 0.5 * Math.Cos(2.0 * Math.PI * u);
        }

        return (now % 1000) < 500 ? 1.0 : 0.0;
    }

    public async Task ResetSettingOverridesAsync(
        VirtualGamepadControllerViewModel item,
        string? key)
    {
        DeviceSettingOverrides current = item.Record.SettingOverrides;
        DeviceSettingOverrides? updated = key switch
        {
            "vibration" => current with { VibrationEnabled = null },
            "autoStart" => current with { AutoStartVirtualGamepad = null },
            "autoHide" => current with { AutoHidePhysicalGamepad = null },
            "led" => current with { Led = null },
            "powerOff" => current with { PowerOffIdleSeconds = null },
            "inputPoll" => current with { InputPollIntervalMs = null },
            "motion" => current with { MotionEnabled = null },
            "motionInterval" => current with { MotionPollIntervalMs = null },
            "battery" => current with
            {
                BatteryAlertEnabled = null,
                BatteryAlertLevel = null,
                BatteryAlertColorEnabled = null,
                BatteryAlertColor = null,
                BatteryAlertEffectEnabled = null,
                BatteryAlertEffect = null,
                BatteryNotifyEnabled = null,
                BatterySoundEnabled = null
            },
            "ignoreExternal" => current with { IgnoreExternalColorSignals = null },
            "keepHidden" => current with { KeepHiddenOnExit = null },
            "virtualType" => current with { VirtualGamepadType = null },
            "restartDevice" => current with { RestartPhysicalDeviceOnVirtualStart = null },
            _ => null
        };
        if (updated is null)
        {
            return;
        }

        ControllerRecord record = item.Record with
        {
            SettingOverrides = updated,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (await SaveRecordAsync(item, record))
        {
            item.SetStatus("El ajuste volvió al valor del preset global.");
        }
    }

    public async Task SaveBoolOverrideAsync(
        VirtualGamepadControllerViewModel item,
        string key,
        bool? value)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar el ajuste.");
            return;
        }

        DeviceSettingOverrides current = item.Record.SettingOverrides;
        DeviceSettingOverrides updated = key switch
        {
            "vibration" => current with { VibrationEnabled = value },
            "autoStart" => current with { AutoStartVirtualGamepad = value },
            "autoHide" => current with { AutoHidePhysicalGamepad = value },
            "motion" => current with { MotionEnabled = value },
            "batteryAlert" => current with { BatteryAlertEnabled = value },
            "batteryNotify" => current with { BatteryNotifyEnabled = value },
            "batterySound" => current with { BatterySoundEnabled = value },
            "batteryColor" => current with { BatteryAlertColorEnabled = value },
            "batteryEffect" => current with { BatteryAlertEffectEnabled = value },
            "ignoreExternal" => current with { IgnoreExternalColorSignals = value },
            "keepHidden" => current with { KeepHiddenOnExit = value },
            "ledColorIntensity" => current with { LedEffectColorIntensity = value },
            "restartDevice" => current with { RestartPhysicalDeviceOnVirtualStart = value },
            _ => current
        };
        if (updated == current)
        {
            return;
        }

        try
        {
            ControllerRecord record = item.Record with
            {
                SettingOverrides = updated,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            if (await SaveRecordAsync(item, record) &&
                (key.StartsWith("battery", StringComparison.Ordinal) ||
                 key == "chargingEffect" ||
                 key == "ledColorIntensity" ||
                 key == "ignoreExternal"))
            {
                _ = ApplyLedStateAsync(item);
                EnsureBatteryEffectTimer();
                EnsureLedGuardTimer();
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar el ajuste: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    public async Task SaveOverrideValueAsync(
        VirtualGamepadControllerViewModel item,
        string key,
        object? value)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se puede guardar el ajuste.");
            return;
        }

        DeviceSettingOverrides current = item.Record.SettingOverrides;
        DeviceSettingOverrides updated = key switch
        {
            "powerOff" => current with { PowerOffIdleSeconds = value as int? },
            "inputPoll" => current with { InputPollIntervalMs = value as int? },
            "motionInterval" => current with { MotionPollIntervalMs = value as int? },
            "batteryLevel" => current with { BatteryAlertLevel = value as int? },
            "batteryColorHex" => current with
            {
                BatteryAlertColor = value is string hex
                    ? ControllerLedColor.NormalizeHex(hex)
                    : null
            },
            "batteryEffectValue" => current with { BatteryAlertEffect = value as string },
            "chargingEffect" => current with
            {
                ChargingLedEffect = value is string effect
                    ? ChargingLedEffects.Normalize(effect)
                    : null
            },
            _ => current
        };
        if (updated == current)
        {
            return;
        }

        try
        {
            ControllerRecord record = item.Record with
            {
                SettingOverrides = updated,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            if (await SaveRecordAsync(item, record) &&
                (key.StartsWith("battery", StringComparison.Ordinal) ||
                 key == "chargingEffect"))
            {
                _ = ApplyLedStateAsync(item);
                EnsureBatteryEffectTimer();
                EnsureLedGuardTimer();
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo guardar el ajuste: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }

    private async Task SaveBatteryAlertSettingsAsync(
        VirtualGamepadControllerViewModel item,
        ControllerSettings settings)
    {
        if (_controllerRepository is null)
        {
            item.SetStatus("No se pueden guardar las opciones de batería.");
            return;
        }

        try
        {
            DeviceSettingOverrides current = item.Record.SettingOverrides;
            ControllerRecord updated = item.Record with
            {
                SettingOverrides = current with
                {
                    BatteryAlertEnabled = current.BatteryAlertEnabled.HasValue ? settings.BatteryAlertEnabled : null,
                    BatteryAlertLevel = current.BatteryAlertLevel.HasValue ? BatteryAlertLevels.Normalize(settings.BatteryAlertLevel) : null,
                    BatteryAlertColorEnabled = current.BatteryAlertColorEnabled.HasValue ? settings.BatteryAlertColorEnabled : null,
                    BatteryAlertColor = current.BatteryAlertColor is not null ? ControllerLedColor.NormalizeHex(settings.BatteryAlertColor) ?? "FF453A" : null,
                    BatteryAlertEffectEnabled = current.BatteryAlertEffectEnabled.HasValue ? settings.BatteryAlertEffectEnabled : null,
                    BatteryAlertEffect = current.BatteryAlertEffect is not null ? BatteryAlertEffects.Normalize(settings.BatteryAlertEffect) : null,
                    BatteryNotifyEnabled = current.BatteryNotifyEnabled.HasValue ? settings.BatteryNotifyEnabled : null,
                    BatterySoundEnabled = current.BatterySoundEnabled.HasValue ? settings.BatterySoundEnabled : null,
                    IgnoreExternalColorSignals = current.IgnoreExternalColorSignals.HasValue ? settings.IgnoreExternalColorSignals : null
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveRecordAsync(item, updated);
            _ = ApplyLedStateAsync(item);
            EnsureBatteryEffectTimer();
            EnsureLedGuardTimer();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudieron guardar las opciones de batería: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
        }
    }
    private async Task TryAutoHidePhysicalDeviceAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device)
    {
        if (!EffectiveSettings(item.Record).AutoHidePhysicalGamepad ||
            string.IsNullOrWhiteSpace(device.DevicePath))
        {
            return;
        }

        if (_autoHideSuppressedControllers.Contains(item.Record.ControllerId))
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Ocultamiento automático omitido: pausado esta sesión porque SDL no pudo leer el mando oculto. " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            return;
        }

        if (IsDeviceHiddenByGdm(device))
        {
            _autoHiddenDeviceKeys.Add(device.DeviceKey);
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"El mando ya estaba oculto por GDM; se adopta para restaurarlo al apagar la salida. " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            return;
        }

        if (_hidePhysicalDevice is null)
        {
            return;
        }

        try
        {
            if (await _hidePhysicalDevice(device))
            {
                _autoHiddenDeviceKeys.Add(device.DeviceKey);
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Mando físico oculto automáticamente al iniciar la salida. " +
                    $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Error,
                $"No se pudo ocultar automáticamente el mando físico. " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.",
                exception);
        }
    }
    private async Task<bool> ConfirmPhysicalDisconnectAsync(ConnectedGamepad device)
    {
        if (_isPhysicalDevicePresent is null)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DevicePresenceState state = _autoHiddenPresenceStates.GetValueOrDefault(device.DeviceKey);
        if (state.LastCheckedAtUtc != default &&
            now - state.LastCheckedAtUtc < TimeSpan.FromMilliseconds(250))
        {
            return false;
        }

        bool isPresent;
        try
        {
            isPresent = _isPhysicalDevicePresent(device);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            _reportError?.Invoke(
                $"No se pudo comprobar si «{device.Name}» sigue conectado físicamente.",
                exception);
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Error,
                $"Falló la comprobación de presencia física. mando={DescribeDevice(device)}.",
                exception);
            _autoHiddenPresenceStates[device.DeviceKey] = new DevicePresenceState(now, 0);
            return false;
        }

        if (isPresent)
        {
            if (state.LastCheckedAtUtc == default || state.MissingChecks > 0)
            {
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Windows confirma que el dispositivo HID sigue presente aunque SDL no entregue entrada. " +
                    $"mando={DescribeDevice(device)}.");
            }

            _autoHiddenPresenceStates[device.DeviceKey] = new DevicePresenceState(now, 0);
            return false;
        }

        int missingChecks = state.MissingChecks + 1;
        _autoHiddenPresenceStates[device.DeviceKey] = new DevicePresenceState(now, missingChecks);
        WriteAutoHideDiagnostic(
            DiagnosticLogLevel.Warning,
            $"Windows no encuentra el dispositivo HID oculto: comprobación {missingChecks}/3; " +
            $"mando={DescribeDevice(device)}.");
        return missingChecks >= 3;
    }

    /// <summary>
    /// Notifica el encendido de la salida virtual: mando físico, tipo de
    /// mando virtual, perfil y, si el perfil tiene un combo «Apagar el mando
    /// físico», la primera combinación encontrada.
    /// </summary>
    private void NotifyVirtualGamepadStarted(
        ConnectedGamepad device,
        string virtualGamepadType,
        ControllerProfile? profile)
    {
        var lines = new List<string>
        {
            $"Mando físico: {device.Name}",
            $"Tipo virtual: {VirtualGamepadTypes.DisplayName(virtualGamepadType)}",
            $"Perfil: {profile?.DisplayName ?? "Perfil por mando"}"
        };

        ControllerCombo? powerOffCombo = profile?.Combos?.FirstOrDefault(
            combo => combo.ActionType == ComboActionTypes.PowerOff);
        if (powerOffCombo is not null)
        {
            lines.Add($"Apagar el mando: {FormatComboButtons(powerOffCombo)}");
        }

        _notifyEvent?.Invoke("Mando virtual encendido", [.. lines]);
    }

    /// <summary>
    /// Notifica el apagado manual/por combo de la salida virtual e indica el
    /// combo para reactivarla si el perfil tiene uno de alternar salida.
    /// </summary>
    private void NotifyVirtualGamepadStopped(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device)
    {
        var lines = new List<string> { $"Mando físico: {device.Name}" };

        ControllerCombo? toggleCombo = FindAssignedProfile(item.Record)?.Combos
            ?.FirstOrDefault(combo => combo.ActionType == ComboActionTypes.StopOutput);
        if (toggleCombo is not null)
        {
            lines.Add($"Para encenderlo: {FormatComboButtons(toggleCombo)}");
        }

        _notifyEvent?.Invoke("Mando virtual apagado", [.. lines]);
    }

    private static string FormatComboButtons(ControllerCombo combo)
    {
        string names = string.Join(
            " + ",
            combo.Buttons.Select(id => GamepadControlCatalog.Buttons
                .FirstOrDefault(option => option.Id == id)?.DisplayName ?? id));
        if (combo.RequiresHold && combo.HoldMilliseconds > 0)
        {
            names += $" durante {combo.HoldMilliseconds / 1000d:0.#} s";
        }

        return names;
    }

    private void WriteAutoHideDiagnostic(
        DiagnosticLogLevel level,
        string message,
        Exception? exception = null) =>
        _writeDiagnostic?.Invoke(level, message, exception);

    private static string DescribeDevice(ConnectedGamepad device) =>
        $"nombre=\"{device.Name}\"; claveSDL={device.DeviceKey}; VID={device.VendorId?.ToString("X4") ?? "(n/d)"}; " +
        $"PID={device.ProductId?.ToString("X4") ?? "(n/d)"}; serial=\"{device.SerialNumber ?? "(n/d)"}\"; " +
        $"ruta=\"{device.DevicePath ?? "(n/d)"}\"; GUID={device.SdlGuid ?? "(n/d)"}";

    private readonly record struct DevicePresenceState(
        DateTimeOffset LastCheckedAtUtc,
        int MissingChecks);

    private ControllerProfile? FindAssignedProfile(ControllerRecord record) =>
        record.ProfileId is Guid profileId
            ? Profiles.FirstOrDefault(profile => profile.ProfileId == profileId)
            : record.DeviceProfile;

    public ControllerSettings EffectiveSettings(ControllerRecord record)
    {
        if (_effectiveSettingsCache.TryGetValue(record, out EffectiveSettingsCacheEntry? cached) &&
            ReferenceEquals(cached.Preset, _globalPreset))
        {
            return cached.Settings;
        }

        GlobalPreset preset = _globalPreset;
        DeviceSettingOverrides overrides = record.SettingOverrides ?? new DeviceSettingOverrides();
        ControllerSettings global = preset.Settings;
        ControllerSettings local = record.Settings;

        ControllerSettings effective = global with
        {
            VibrationEnabled = overrides.VibrationEnabled ?? global.VibrationEnabled,
            AutoStartVirtualGamepad =
                overrides.AutoStartVirtualGamepad ?? global.AutoStartVirtualGamepad,
            AutoHidePhysicalGamepad =
                overrides.AutoHidePhysicalGamepad ?? global.AutoHidePhysicalGamepad,
            KeepHiddenOnExit = overrides.KeepHiddenOnExit ?? global.KeepHiddenOnExit,
            Led = ControllerLedSettings.Normalize(overrides.Led ?? global.Led),
            PowerOffIdleSeconds =
                overrides.PowerOffIdleSeconds ?? global.PowerOffIdleSeconds,
            InputPollIntervalMs =
                overrides.InputPollIntervalMs ?? global.InputPollIntervalMs,
            MotionEnabled = overrides.MotionEnabled ?? global.MotionEnabled,
            MotionPollIntervalMs =
                overrides.MotionPollIntervalMs ?? global.MotionPollIntervalMs,
            BatteryAlertEnabled =
                overrides.BatteryAlertEnabled ?? global.BatteryAlertEnabled,
            BatteryAlertLevel =
                overrides.BatteryAlertLevel ?? global.BatteryAlertLevel,
            BatteryAlertColorEnabled =
                overrides.BatteryAlertColorEnabled ?? global.BatteryAlertColorEnabled,
            BatteryAlertColor =
                overrides.BatteryAlertColor ?? global.BatteryAlertColor,
            BatteryAlertEffectEnabled =
                overrides.BatteryAlertEffectEnabled ?? global.BatteryAlertEffectEnabled,
            BatteryAlertEffect =
                overrides.BatteryAlertEffect ?? global.BatteryAlertEffect,
            ChargingLedEffect = ChargingLedEffects.Normalize(
                overrides.ChargingLedEffect ?? global.ChargingLedEffect),
            LedEffectColorIntensity =
                overrides.LedEffectColorIntensity ?? global.LedEffectColorIntensity,
            BatteryNotifyEnabled =
                overrides.BatteryNotifyEnabled ?? global.BatteryNotifyEnabled,
            BatterySoundEnabled =
                overrides.BatterySoundEnabled ?? global.BatterySoundEnabled,
            IgnoreExternalColorSignals =
                overrides.IgnoreExternalColorSignals ?? global.IgnoreExternalColorSignals,
            RestartPhysicalDeviceOnVirtualStart =
                overrides.RestartPhysicalDeviceOnVirtualStart ??
                global.RestartPhysicalDeviceOnVirtualStart,
            MotionCalibrated = local.MotionCalibrated,
            MotionGyroBiasPitchRadS = local.MotionGyroBiasPitchRadS,
            MotionGyroBiasYawRadS = local.MotionGyroBiasYawRadS,
            MotionGyroBiasRollRadS = local.MotionGyroBiasRollRadS,
            MotionInvertGyroPitch = local.MotionInvertGyroPitch,
            MotionInvertGyroYaw = local.MotionInvertGyroYaw,
            MotionInvertGyroRoll = local.MotionInvertGyroRoll
        };

        _effectiveSettingsCache.Remove(record);
        _effectiveSettingsCache.Add(
            record,
            new EffectiveSettingsCacheEntry(_globalPreset, effective));
        return effective;
    }

    private async Task LoadGlobalPresetAsync()
    {
        if (_globalPresetRepository is null)
        {
            return;
        }

        try
        {
            _globalPreset = await _globalPresetRepository.LoadAsync();
        }
        catch (InvalidDataException exception)
        {
            ReportFailure("El ajuste global guardado no se pudo validar.", exception);
        }
        catch (IOException exception)
        {
            ReportFailure("No se pudo leer el ajuste global.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReportFailure("No hay permisos para leer el ajuste global.", exception);
        }
    }

    private async Task EnsureGlobalPresetProfileAsync()
    {
        if (_profileRepository is null ||
            Profiles.Any(ControllerProfile.IsGlobalPreset))
        {
            return;
        }

        ControllerProfile? seed = Profiles.FirstOrDefault(profile =>
                string.Equals(
                    profile.DisplayName,
                    BuiltinProfileName,
                    StringComparison.OrdinalIgnoreCase)) ??
            Profiles.FirstOrDefault(profile => !ControllerProfile.IsGlobalPreset(profile));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ControllerProfile profile = seed is not null
            ? seed with
            {
                ProfileId = ControllerProfile.GlobalPresetId,
                DisplayName = ControllerProfile.GlobalPresetName,
                Description = ControllerProfile.GlobalPresetDescription,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }
            : new ControllerProfile
            {
                ProfileId = ControllerProfile.GlobalPresetId,
                DisplayName = ControllerProfile.GlobalPresetName,
                Description = ControllerProfile.GlobalPresetDescription,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

        try
        {
            await _profileRepository.SaveAsync(profile);
            Profiles.Insert(0, profile);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ReportFailure("No se pudo crear el perfil de ajuste global.", exception);
        }
    }

    private async Task RemoveLegacyBuiltinProfilesAsync()
    {
        if (_profileRepository is null)
        {
            return;
        }

        List<ControllerProfile> legacy = Profiles
            .Where(profile =>
                !ControllerProfile.IsGlobalPreset(profile) &&
                string.Equals(
                    profile.DisplayName,
                    BuiltinProfileName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (legacy.Count == 0)
        {
            return;
        }

        foreach (ControllerProfile profile in legacy)
        {
            _legacyBuiltinProfileIds.Add(profile.ProfileId);
            Profiles.Remove(profile);
            try
            {
                await _profileRepository.DeleteAsync(profile.ProfileId);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException)
            {
                ReportFailure("No se pudo eliminar el perfil «Predeterminado» heredado.", exception);
            }
        }

        RebuildProfileEditorTabs();
        RebuildAssignmentChoices();
        await RemapLegacyBuiltinAssignmentsAsync();
    }

    private async Task RemapLegacyBuiltinAssignmentsAsync()
    {
        if (_legacyBuiltinProfileIds.Count == 0)
        {
            return;
        }

        bool changed = false;
        if (_controllerRepository is not null)
        {
            foreach (ControllerRecord record in SavedControllers.ToList())
            {
                if (record.ProfileId is not Guid profileId ||
                    !_legacyBuiltinProfileIds.Contains(profileId))
                {
                    continue;
                }

                ControllerRecord updated = record with
                {
                    ProfileId = ControllerProfile.GlobalPresetId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                try
                {
                    await _controllerRepository.SaveAsync(updated);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or
                    IOException or
                    UnauthorizedAccessException)
                {
                    ReportFailure("No se pudo migrar la asignación de perfil de un mando.", exception);
                    continue;
                }

                int index = SavedControllers.IndexOf(record);
                if (index >= 0)
                {
                    SavedControllers[index] = updated;
                }

                changed = true;
            }
        }

        for (int index = 0; index < _temporaryRecords.Count; index++)
        {
            ControllerRecord record = _temporaryRecords[index];
            if (record.ProfileId is Guid profileId &&
                _legacyBuiltinProfileIds.Contains(profileId))
            {
                _temporaryRecords[index] = record with
                {
                    ProfileId = ControllerProfile.GlobalPresetId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                changed = true;
            }
        }

        if (changed)
        {
            RebuildAssignmentChoices();
            SynchronizeVirtualGamepads();
        }
    }

    public async Task SaveGlobalPresetAsync(GlobalPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (_globalPresetRepository is null)
        {
            StatusMessage = "El almacenamiento del ajuste global no está configurado.";
            return;
        }

        IsBusy = true;
        try
        {
            await _globalPresetRepository.SaveAsync(preset);
            _globalPreset = preset;
            GlobalPresetEditor?.LoadFrom(preset);
            foreach (VirtualGamepadControllerViewModel item in VirtualGamepads)
            {
                item.RefreshEffectiveSettings();
            }
            OnPropertyChanged(nameof(HasAutoStartVirtualGamepad));
            StatusMessage = "Ajuste global guardado.";
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ReportFailure("No se pudo guardar el ajuste global.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyVirtualOutputStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyVirtualGamepadRunning));
    }

    private void UpdateSelectedAssignmentChoice()
    {
        Guid? profileId = SelectedController?.ProfileId;
        SelectedAssignmentProfile = AssignmentChoices.FirstOrDefault(
            choice => choice.ProfileId == profileId) ??
            AssignmentChoices.FirstOrDefault(choice => choice.ProfileId is not null) ??
            AssignmentChoices.FirstOrDefault();
    }

    private void ReportFailure(string message, Exception exception)
    {
        StatusMessage = $"{message} {exception.Message}";
        _reportError?.Invoke(message, exception);
    }

    private void ReportCommandFailure(Exception exception)
    {
        ReportFailure("No se pudo completar la acción.", exception);
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        _createCommand.NotifyCanExecuteChanged();
        _saveCommand.NotifyCanExecuteChanged();
        _duplicateCommand.NotifyCanExecuteChanged();
        _deleteCommand.NotifyCanExecuteChanged();
        _assignCommand.NotifyCanExecuteChanged();
    }

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class ParameterCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            execute(parameter);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class AsyncCommand(
        Func<Task> execute,
        Func<bool> canExecute,
        Action<Exception>? onError = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public async void Execute(object? parameter)
        {
            try
            {
                await execute();
            }
            catch (Exception exception)
            {
                onError?.Invoke(exception);
            }
        }

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}