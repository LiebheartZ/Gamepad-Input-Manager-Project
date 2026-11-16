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
    private readonly Func<string>? _defaultVirtualGamepadType;
    private readonly Func<bool>? _restoreGamepadsOnExit;
    private readonly AsyncCommand _createCommand;
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _duplicateCommand;
    private readonly AsyncCommand _deleteCommand;
    private readonly AsyncCommand _assignCommand;
    private readonly GamepadComboEvaluator _comboEvaluator = new();
    private ControllerListItem[] _connectedControllerItems = [];
    private readonly Dictionary<Guid, string> _autoStartAttempts = [];
    private readonly List<Task> _startupAutoStartTasks = [];
    private readonly HashSet<string> _autoHiddenDeviceKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (VirtualGamepadControllerViewModel Item, ConnectedGamepad Device)>
        _deferredAutomaticHides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevicePresenceState> _autoHiddenPresenceStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DevicePresenceState> _hiddenInputUnavailableStates = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _automaticHideBlockedControllerIds = [];
    private readonly Dictionary<Guid, string> _lastRebindDiagnostics = [];
    private ControllerProfile? _selectedProfile;
    private ControllerRecord? _selectedController;
    private VirtualGamepadControllerViewModel? _selectedVirtualGamepad;
    private ProfileChoice? _selectedAssignmentProfile;
    private string _draftName = string.Empty;
    private string _draftDescription = string.Empty;
    private string _draftVirtualGamepadType = VirtualGamepadTypes.Default;
    private double _leftStickDeadZone;
    private double _rightStickDeadZone;
    private string _statusMessage = "Los perfiles aún no se han cargado.";
    private bool _isBusy;
    private bool _isInitialized;
    private bool _controllersLoaded;
    private bool _automaticHidingReady;
    private bool _elevationRequested;
    private Task? _initializationTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ControllerProfile> Profiles { get; } = [];
    public ObservableCollection<ControllerRecord> SavedControllers { get; } = [];
    public ObservableCollection<ProfileChoice> AssignmentChoices { get; } = [];
    public ObservableCollection<ProfileMappingRow> ButtonMappingRows { get; } = [];
    public ObservableCollection<ProfileMappingRow> AxisMappingRows { get; } = [];
    public ObservableCollection<ProfileComboRow> ComboRows { get; } = [];
    public IReadOnlyList<VirtualGamepadTypeOption> VirtualGamepadTypeOptions { get; } =
        VirtualGamepadTypes.Options;

    public ObservableCollection<VirtualGamepadControllerViewModel> VirtualGamepads { get; } = [];
    public ICommand CreateCommand => _createCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand DuplicateCommand => _duplicateCommand;
    public ICommand DeleteCommand => _deleteCommand;
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
    public bool HasAutoStartVirtualGamepad =>
        SavedControllers.Any(controller => controller.Settings.AutoStartVirtualGamepad);

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
            if (SetProperty(ref _draftVirtualGamepadType, value))
            {
                RefreshButtonMappingTargets();
            }
        }
    }

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

            DraftName = value?.DisplayName ?? string.Empty;
            DraftDescription = value?.Description ?? string.Empty;
            DraftVirtualGamepadType = value?.VirtualGamepadType is { } type &&
                                      VirtualGamepadTypes.IsSupported(type)
                ? type
                : ResolveDefaultVirtualGamepadType();
            LoadMappingRows(value);
            NotifyCommandsCanExecuteChanged();
        }
    }

    public ControllerRecord? SelectedController
    {
        get => _selectedController;
        set
        {
            if (!SetProperty(ref _selectedController, value))
            {
                return;
            }

            UpdateSelectedAssignmentChoice();
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
                    ControllerProfile? match = Profiles.FirstOrDefault(
                        profile => profile.ProfileId == profileId);
                    if (match is not null)
                    {
                        SelectedProfile = match;
                    }
                }

                _assignCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public VirtualGamepadControllerViewModel? SelectedVirtualGamepad
    {
        get => _selectedVirtualGamepad;
        set
        {
            if (SetProperty(ref _selectedVirtualGamepad, value))
            {
                SelectedController = value?.Record;
            }
        }
    }

    public bool HasVirtualGamepads => VirtualGamepads.Count > 0;
    public bool ShowNoVirtualGamepads => VirtualGamepads.Count == 0;

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
        Func<bool>? restoreGamepadsOnExit = null)
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
        _defaultVirtualGamepadType = defaultVirtualGamepadType;
        _restoreGamepadsOnExit = restoreGamepadsOnExit;
        _createCommand = new AsyncCommand(CreateAsync, () => CanEditProfiles);
        _saveCommand = new AsyncCommand(SaveAsync, () =>
            CanEditProfiles && SelectedProfile is not null &&
            !string.IsNullOrWhiteSpace(DraftName) && DraftName.Trim().Length <= 80);
        _duplicateCommand = new AsyncCommand(DuplicateAsync, () =>
            CanEditProfiles && SelectedProfile is not null);
        _deleteCommand = new AsyncCommand(DeleteAsync, () =>
            CanEditProfiles && SelectedProfile is not null);
        _assignCommand = new AsyncCommand(AssignAsync, () =>
            !IsBusy && _controllerRepository is not null &&
            SelectedController is not null && SelectedAssignmentProfile is not null);
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

                continue;
            }

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
                            _automaticHideBlockedControllerIds.Add(item.Record.ControllerId);
                            bool physicalDeviceRestored =
                                await RestoreAutoHiddenDeviceAsync(device);
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

                ControllerProfile? profile = FindAssignedProfile(item.Record);
                _hiddenInputUnavailableStates.Remove(device.DeviceKey);
                ComboEvaluation comboEvaluation = _comboEvaluator.Evaluate(
                    device.DeviceKey,
                    snapshot,
                    profile?.Combos);
                _virtualGamepadService.SubmitInputSnapshot(
                    device,
                    snapshot,
                    profile,
                    GetComboTargetButtons(comboEvaluation));
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
    }

    public const string BuiltinProfileName = "Predeterminado";

    public async Task<ControllerProfile?> EnsureBuiltinProfileAsync()
    {
        if (_profileRepository is null || !_isInitialized || IsBusy)
        {
            return null;
        }

        if (Profiles.Any(profile =>
                string.Equals(
                    profile.DisplayName,
                    BuiltinProfileName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var profile = new ControllerProfile
        {
            ProfileId = Guid.NewGuid(),
            DisplayName = BuiltinProfileName,
            Description = "Perfil creado automáticamente por GDM. Puedes modificarlo como cualquier otro perfil.",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        try
        {
            await _profileRepository.SaveAsync(profile);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            ReportFailure("No se pudo crear el perfil predeterminado.", exception);
            return null;
        }

        int insertAt = Profiles.TakeWhile(candidate =>
            string.Compare(
                candidate.DisplayName,
                profile.DisplayName,
                StringComparison.CurrentCultureIgnoreCase) < 0).Count();
        Profiles.Insert(insertAt, profile);
        RebuildAssignmentChoices();
        return profile;
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

        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        _automaticHidingReady = true;
        foreach ((VirtualGamepadControllerViewModel item, ConnectedGamepad device) in
                 _deferredAutomaticHides.Values.ToArray())
        {
            if (item.ConnectedDevice?.DeviceKey == device.DeviceKey &&
                _virtualGamepadService?.IsRunningFor(device.DeviceKey) == true &&
                item.Record.Settings.AutoHidePhysicalGamepad)
            {
                WriteAutoHideDiagnostic(
                    DiagnosticLogLevel.Information,
                    $"Aplicando ocultamiento diferido tras el inicio. Controlador={item.Record.ControllerId}; " +
                    $"mando={DescribeDevice(device)}.");
                await ApplyAutomaticHideAsync(item, device);
            }
        }

        _deferredAutomaticHides.Clear();
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
            VirtualGamepadType = VirtualGamepadTypes.IsSupported(DraftVirtualGamepadType)
                ? DraftVirtualGamepadType
                : ResolveDefaultVirtualGamepadType(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        await SaveNewProfileAsync(profile, "Perfil creado.");
    }

    private async Task SaveAsync()
    {
        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy)
        {
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

        IsBusy = true;
        try
        {
            ControllerProfile updated = selected with
            {
                DisplayName = name,
                Description = DraftDescription.Trim(),
                ButtonMappings = CreateMappingDictionary(
                    selected.ButtonMappings,
                    ButtonMappingRows),
                AxisMappings = CreateMappingDictionary(
                    selected.AxisMappings,
                    AxisMappingRows),
                AxisInversions = CreateInversionDictionary(
                    selected.AxisInversions,
                    AxisMappingRows),
                DeadZones = CreateDeadZoneDictionary(
                    selected.DeadZones,
                    LeftStickDeadZone,
                    RightStickDeadZone),
                VirtualGamepadType = VirtualGamepadTypes.IsSupported(DraftVirtualGamepadType)
                    ? DraftVirtualGamepadType
                    : selected.VirtualGamepadType,
                Combos = combos,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
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

    private async Task DuplicateAsync()
    {
        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy)
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
            DeadZones = new Dictionary<string, double>(selected.DeadZones),
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
        if (_profileRepository is null || SelectedProfile is not { } selected || IsBusy)
        {
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
            SelectedProfile = Profiles.FirstOrDefault();
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
        if (_controllerRepository is null ||
            SelectedController is not { } controller ||
            SelectedAssignmentProfile is not { } choice ||
            IsBusy)
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
            await _controllerRepository.SaveAsync(updated);
            int index = SavedControllers.IndexOf(controller);
            if (index >= 0)
            {
                SavedControllers[index] = updated;
            }

            SelectedController = updated;
            _onControllerUpdated(updated);
            StatusMessage = choice.ProfileId is null
                ? $"Se quitó la asignación de perfil a «{controller.DisplayName}»."
                : $"Se asignó «{choice.DisplayName}» a «{controller.DisplayName}».";
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

    private void RebuildAssignmentChoices()
    {
        Guid? selectedId = SelectedAssignmentProfile?.ProfileId;
        AssignmentChoices.Clear();
        AssignmentChoices.Add(new ProfileChoice(null, "Sin perfil"));
        foreach (ControllerProfile profile in Profiles)
        {
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

    private void LoadMappingRows(ControllerProfile? profile)
    {
        ButtonMappingRows.Clear();
        foreach (GamepadControlOption source in GamepadControlCatalog.Buttons)
        {
            string target = profile?.ButtonMappings.TryGetValue(source.Id, out string? mappedTarget) == true
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
            string target = profile?.AxisMappings.TryGetValue(source.Id, out string? mappedTarget) == true
                ? NormalizeAxisTarget(mappedTarget, source.Id)
                    : source.Id;
            AxisMappingRows.Add(new ProfileMappingRow(
                source,
                GamepadControlCatalog.Axes,
                target,
                profile?.AxisInversions.TryGetValue(source.Id, out bool isInverted) == true &&
                    isInverted));
        }

        LeftStickDeadZone = profile?.DeadZones.GetValueOrDefault("leftStick") ?? 0d;
        RightStickDeadZone = profile?.DeadZones.GetValueOrDefault("rightStick") ?? 0d;

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
    }

    private void RefreshButtonMappingTargets()
    {
        IReadOnlyList<GamepadControlOption> targets =
            GamepadControlCatalog.GetButtonTargetsFor(DraftVirtualGamepadType);
        foreach (ProfileComboRow row in ComboRows)
        {
            row.RebuildTargetButtons(targets);
        }

        foreach (ProfileMappingRow row in ButtonMappingRows)
        {
            row.Targets.Clear();
            foreach (GamepadControlOption target in targets)
            {
                row.Targets.Add(target);
            }

            if (targets.Any(target => target.Id == row.SelectedTargetId))
            {
                continue;
            }

            GamepadControlOption? current = GamepadControlCatalog.ButtonTargets
                .FirstOrDefault(target => target.Id == row.SelectedTargetId);
            if (current is not null)
            {
                row.Targets.Add(new GamepadControlOption(
                    current.Id,
                    $"{current.DisplayName} (no compatible)"));
            }
        }
    }

    private string ResolveDefaultVirtualGamepadType()
    {
        string? defaultType = _defaultVirtualGamepadType?.Invoke();
        return VirtualGamepadTypes.IsSupported(defaultType)
            ? defaultType!
            : VirtualGamepadTypes.Default;
    }

    private string ResolveVirtualGamepadType(ControllerRecord record)
    {
        string? profileType = FindAssignedProfile(record)?.VirtualGamepadType;
        return VirtualGamepadTypes.IsSupported(profileType)
            ? profileType!
            : ResolveDefaultVirtualGamepadType();
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
        var savedIds = SavedControllers
            .Select(controller => controller.ControllerId)
            .ToHashSet();
        foreach (VirtualGamepadControllerViewModel item in VirtualGamepads.ToArray())
        {
            if (savedIds.Contains(item.Record.ControllerId))
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

        foreach (ControllerRecord record in SavedControllers)
        {
            ControllerListItem? controllerItem = _connectedControllerItems.FirstOrDefault(
                candidate => candidate.SavedRecord?.ControllerId == record.ControllerId);
            ConnectedGamepad? connectedDevice = controllerItem?.ConnectedDevice;
            VirtualGamepadControllerViewModel? output = VirtualGamepads.FirstOrDefault(
                candidate => candidate.Record.ControllerId == record.ControllerId);
            if (output is null)
            {
                output = new VirtualGamepadControllerViewModel(
                    record,
                    StartVirtualGamepadAsync,
                    StopVirtualGamepadAsync,
                    ToggleAutoStartVirtualGamepadAsync,
                    ToggleAutoHidePhysicalGamepadAsync,
                    ToggleKeepHiddenOnExitAsync);
                VirtualGamepads.Add(output);
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

            string profileName = FindAssignedProfile(record)?.DisplayName ?? "Sin perfil";
            output.Update(
                record,
                connectedDevice,
                profileName,
                running,
                ResolveVirtualGamepadType(record));
            if (_isInitialized &&
                _controllersLoaded &&
                record.Settings.AutoStartVirtualGamepad &&
                connectedDevice is not null &&
                !running &&
                !output.IsStarting &&
                _autoStartAttempts.GetValueOrDefault(record.ControllerId) != connectedDevice.DeviceKey)
            {
                _autoStartAttempts[record.ControllerId] = connectedDevice.DeviceKey;
                _startupAutoStartTasks.Add(StartVirtualGamepadAsync(output));
            }

            if (connectedDevice is null)
            {
                _autoStartAttempts.Remove(record.ControllerId);
            }
        }

        VirtualGamepadControllerViewModel? desiredSelection = VirtualGamepads.FirstOrDefault(
                item => item.Record.ControllerId == SelectedController?.ControllerId) ??
            VirtualGamepads.FirstOrDefault();
        if (!ReferenceEquals(_selectedVirtualGamepad, desiredSelection))
        {
            SelectedVirtualGamepad = desiredSelection;
        }

        OnPropertyChanged(nameof(HasVirtualGamepads));
        OnPropertyChanged(nameof(ShowNoVirtualGamepads));
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

        item.SetStarting(true);
        try
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"Inicio de salida virtual. Controlador={item.Record.ControllerId}; " +
                $"autoInicio={item.Record.Settings.AutoStartVirtualGamepad}; " +
                $"autoOcultar={item.Record.Settings.AutoHidePhysicalGamepad}; mando={DescribeDevice(device)}.");
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
                profile,
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
                profile?.DisplayName ?? "Sin perfil",
                isRunning: true,
                virtualGamepadType);
            NotifyVirtualOutputStateChanged();
            if (item.Record.Settings.AutoHidePhysicalGamepad)
            {
                await ApplyAutomaticHideAsync(item, device);
            }
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

    private async Task StopVirtualGamepadAsync(VirtualGamepadControllerViewModel item)
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
            _comboEvaluator.Reset(device.DeviceKey);
            await RestoreAutoHiddenDeviceAsync(device);

            item.SetStatus("La salida virtual de este mando está detenida.");
            item.Update(
                item.Record,
                item.ConnectedDevice,
                item.ProfileName,
                isRunning: false,
                ResolveVirtualGamepadType(item.Record));
            NotifyVirtualOutputStateChanged();
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException)
        {
            item.SetStatus($"No se pudo detener su salida virtual: {exception.Message}");
            _reportError?.Invoke(item.StatusMessage, exception);
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

    private async Task ApplyComboActionsAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device,
        GamepadInputSnapshot snapshot,
        ControllerProfile? profile,
        ComboEvaluation evaluation)
    {
        if (_virtualGamepadService is null || evaluation.TriggeredCombos.Count == 0)
        {
            return;
        }

        foreach (ControllerCombo combo in evaluation.TriggeredCombos)
        {
            if (combo.ActionType == ComboActionTypes.StopOutput)
            {
                item.SetStatus($"Combo «{combo.DisplayName}»: se detuvo la salida virtual.");
                await StopVirtualGamepadAsync(item);
                return;
            }

            if (combo.ActionType == ComboActionTypes.SwitchType)
            {
                await SwitchVirtualGamepadTypeAsync(item, device, snapshot, profile, combo);
                return;
            }
        }
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
            return;
        }

        string newType = VirtualGamepadTypes.Other(currentType);
        try
        {
            await service.StopAsync(device.DeviceKey);
            await service.StartAsync(device, snapshot, profile, newType);
            item.SetStatus(
                $"Combo «{combo.DisplayName}»: mando virtual {VirtualGamepadTypes.DisplayName(newType)} activo.");
            item.Update(
                item.Record,
                device,
                profile?.DisplayName ?? "Sin perfil",
                isRunning: true,
                newType);
            NotifyVirtualOutputStateChanged();
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
                await service.StartAsync(device, snapshot, profile, currentType);
                item.Update(
                    item.Record,
                    device,
                    profile?.DisplayName ?? "Sin perfil",
                    isRunning: true,
                    currentType);
                NotifyVirtualOutputStateChanged();
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
                Settings = item.Record.Settings with
                {
                    AutoStartVirtualGamepad = !item.Record.Settings.AutoStartVirtualGamepad
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _controllerRepository.SaveAsync(updated);
            int index = SavedControllers.IndexOf(item.Record);
            if (index >= 0)
            {
                SavedControllers[index] = updated;
            }

            item.UpdateRecord(updated);
            _onControllerUpdated(updated);
            item.SetStatus(updated.Settings.AutoStartVirtualGamepad
                ? "Se iniciará automáticamente cuando conectes este mando."
                : "El inicio automático quedó desactivado.");
            startNow = updated.Settings.AutoStartVirtualGamepad && item.ConnectedDevice is not null;
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
            bool autoHideEnabled = !item.Record.Settings.AutoHidePhysicalGamepad;
            ControllerRecord updated = item.Record with
            {
                Settings = item.Record.Settings with
                {
                    AutoHidePhysicalGamepad = autoHideEnabled,
                    KeepHiddenOnExit = autoHideEnabled && item.Record.Settings.KeepHiddenOnExit
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _controllerRepository.SaveAsync(updated);
            int index = SavedControllers.IndexOf(item.Record);
            if (index >= 0)
            {
                SavedControllers[index] = updated;
            }

            item.UpdateRecord(updated);
            _onControllerUpdated(updated);
            if (updated.Settings.AutoHidePhysicalGamepad)
            {
                if (item.ConnectedDevice is { } device && item.IsRunning)
                {
                    await ApplyAutomaticHideAsync(item, device);
                }
                else
                {
                    item.SetStatus("Se ocultará automáticamente después de iniciar su mando virtual.");
                }
            }
            else if (item.ConnectedDevice is { } device &&
                     _autoHiddenDeviceKeys.Contains(device.DeviceKey) &&
                     _showPhysicalDevice is not null &&
                     await _showPhysicalDevice(device))
            {
                _autoHiddenDeviceKeys.Remove(device.DeviceKey);
                item.SetStatus("Ocultamiento automático desactivado; el mando físico volvió a mostrarse.");
            }
            else
            {
                item.SetStatus("El ocultamiento automático quedó desactivado.");
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
    }

    private async Task ToggleKeepHiddenOnExitAsync(VirtualGamepadControllerViewModel item)
    {
        if (_controllerRepository is null || item.IsStarting)
        {
            item.SetStatus("No se puede guardar la opción de mantener oculto el mando.");
            return;
        }

        item.SetStarting(true);
        try
        {
            ControllerRecord updated = item.Record with
            {
                Settings = item.Record.Settings with
                {
                    KeepHiddenOnExit = !item.Record.Settings.KeepHiddenOnExit
                },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _controllerRepository.SaveAsync(updated);
            int index = SavedControllers.IndexOf(item.Record);
            if (index >= 0)
            {
                SavedControllers[index] = updated;
            }

            item.UpdateRecord(updated);
            _onControllerUpdated(updated);
            item.SetStatus(updated.Settings.KeepHiddenOnExit
                ? "El mando permanecerá oculto al cerrar GDM y se recuperará en el siguiente inicio."
                : "El mando volverá a mostrarse al cerrar GDM.");
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

    public IReadOnlyDictionary<string, bool> GetAutoHiddenDeviceExitPolicies()
    {
        bool restoreAll = _restoreGamepadsOnExit?.Invoke() ?? true;
        return VirtualGamepads
            .Where(item =>
                item.ConnectedDevice is { DevicePath: not null } device &&
                _autoHiddenDeviceKeys.Contains(device.DeviceKey))
            .ToDictionary(
                item => item.ConnectedDevice!.DevicePath!,
                item => item.Record.Settings.KeepHiddenOnExit || !restoreAll,
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task ApplyAutomaticHideAsync(
        VirtualGamepadControllerViewModel item,
        ConnectedGamepad device)
    {
        if (_automaticHideBlockedControllerIds.Contains(item.Record.ControllerId))
        {
            item.SetStatus(
                "El ocultamiento automático está pausado durante esta sesión porque SDL dejó de leer este mando cuando se ocultó.");
            return;
        }

        if (!_automaticHidingReady)
        {
            _deferredAutomaticHides[device.DeviceKey] = (item, device);
            item.SetStatus("Mando virtual activo; se ocultará al terminar de iniciar GDM.");
            return;
        }

        if (_hidePhysicalDevice is null)
        {
            item.SetStatus("La salida virtual está activa, pero el servicio de HidHide no está disponible.");
            return;
        }

        if (await _hidePhysicalDevice(device))
        {
            _autoHiddenDeviceKeys.Add(device.DeviceKey);
            _autoHiddenPresenceStates.Remove(device.DeviceKey);
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Information,
                $"HidHide confirmó ocultamiento automático. mando={DescribeDevice(device)}.");
            item.SetStatus("Mando virtual activo; mando físico oculto automáticamente.");
        }
        else
        {
            WriteAutoHideDiagnostic(
                DiagnosticLogLevel.Warning,
                $"El servicio HidHide rechazó o no confirmó el ocultamiento automático. " +
                $"Controlador={item.Record.ControllerId}; mando={DescribeDevice(device)}.");
            item.SetStatus("Mando virtual activo. No se pudo aplicar el ocultamiento automático; revisa la pestaña HidHide.");
        }
    }

    private async Task<bool> RestoreAutoHiddenDeviceAsync(ConnectedGamepad device)
    {
        _deferredAutomaticHides.Remove(device.DeviceKey);
        if (!_autoHiddenDeviceKeys.Contains(device.DeviceKey))
        {
            return true;
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
            : null;

    private void NotifyVirtualOutputStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyVirtualGamepadRunning));
    }

    private void UpdateSelectedAssignmentChoice()
    {
        Guid? profileId = SelectedController?.ProfileId;
        SelectedAssignmentProfile = AssignmentChoices.FirstOrDefault(
            choice => choice.ProfileId == profileId) ??
            AssignmentChoices.FirstOrDefault();
    }

    private void ReportFailure(string message, Exception exception)
    {
        StatusMessage = $"{message} {exception.Message}";
        _reportError?.Invoke(message, exception);
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

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
