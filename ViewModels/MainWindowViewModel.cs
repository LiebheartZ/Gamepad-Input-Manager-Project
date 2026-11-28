using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GDM.Models;
using GDM.Services;
using GDM.Services.Abstractions;
using GDM.Views;

namespace GDM.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan NormalDevicePollingInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VirtualOutputDevicePollingInterval = TimeSpan.FromMilliseconds(500);
    private const int HiddenIdleDeviceRefreshMs = 10000;
    private long _lastHiddenDeviceRefreshMs;

    private TimeSpan VirtualInputPollInterval =>
        TimeSpan.FromMilliseconds(Math.Clamp(_settingsViewModel?.VirtualInputPollIntervalMs ?? 8, 4, 100));

    private TimeSpan EffectiveVirtualInputPollInterval =>
        TimeSpan.FromMilliseconds(Profiles.MinRunningVirtualPollIntervalMs() is int minMs
            ? Math.Min(VirtualInputPollInterval.TotalMilliseconds, minMs)
            : VirtualInputPollInterval.TotalMilliseconds);

    private void OnVirtualPollIntervalChanged(object? sender, EventArgs e)
    {
        if (Profiles.IsAnyVirtualGamepadRunning || Tester.IsVirtualGamepadRunning)
        {
            _testerInputTimer.Interval = EffectiveVirtualInputPollInterval;
        }
    }
    private readonly IGamepadDetectionService? _gamepadDetectionService;
    private readonly IAppLogService? _appLogService;
    private readonly IControllerRepository? _controllerRepository;
    private readonly Func<ControllerRecord, bool>? _confirmRemoveSavedController;
    private readonly SettingsViewModel? _settingsViewModel;
    private readonly TesterViewModel _testerViewModel;
    private readonly Dispatcher _dispatcher;
    private readonly AsyncCommand _refreshDevicesCommand;
    private readonly AsyncParameterCommand _saveControllerCommand;
    private readonly AsyncParameterCommand _removeSavedControllerCommand;
    private readonly IProfileRepository? _profileRepository;
    private readonly IHidHideService _hidHideService;
    private readonly ToastNotificationService _toastService;
    private readonly Func<ControllerProfile, bool>? _confirmDeleteProfile;
    private readonly Func<Guid?>? _defaultProfileId;
    private readonly System.Threading.Timer? _pollTimer;
    private readonly System.Threading.Timer? _deviceEventTimer;
    private readonly DispatcherTimer _testerInputTimer;
    private readonly DispatcherTimer _idlePowerOffTimer;
    private readonly DispatcherTimer _bluetoothStatusTimer;
    private readonly Dictionary<Guid, string> _sessionSavedDeviceKeys = [];
    private Task? _controllerInitializationTask;
    private CancellationTokenSource? _refreshCancellation;
    private ConnectedGamepad[]? _lastLoggedDevices;
    private readonly HashSet<string> _redundantHideAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceNotifyTrack> _deviceNotifyStates =
        new(StringComparer.Ordinal);
    private bool _deviceNotifyBaselineDone;
    private bool _bluetoothWarningShown;
    private const long DeviceDisconnectNotifyGraceMs = 4000;
    private int _isPollQueued;
    private int _isTesterTickRunning;
    private bool _isRefreshing;
    private bool _isMonitoring;
    private bool _isControllerDataLoaded;
    private bool _isSavingController;
    private bool _isRemovingController;
    private bool _hasInitializedAutoStart;
    private bool _hasInitializedDeviceDetection;
    private bool _startupUpdateCheckStarted;
    private string _currentSection = "Inicio";
    private string _sectionTitle = "Inicio";
    private string _sectionDescription = "Base de la nueva versión de Gamepad Device Manager.";
    private string _statusTitle = "La arquitectura inicial está lista";
    private string _statusMessage =
        "La aplicación está organizada por categorías. La detección y configuración de mandos se implementarán en fases posteriores.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand NavigateCommand { get; }
    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;
    public ICommand SaveControllerCommand => _saveControllerCommand;
    public ICommand RemoveSavedControllerCommand => _removeSavedControllerCommand;
    public ObservableCollection<ConnectedGamepad> ConnectedGamepads { get; } = [];
    public ObservableCollection<ControllerRecord> SavedControllers { get; } = [];
    public ObservableCollection<ControllerListItem> ControllerItems { get; } = [];
    public ObservableCollection<DiagnosticLogEntry> LogEntries { get; } = [];
    public SettingsViewModel? Settings => _settingsViewModel;
    public TesterViewModel Tester => _testerViewModel;
    public ProfilesViewModel Profiles { get; }
    public UpdateViewModel Updates { get; }
    public HidHideViewModel HidHide { get; }
    public BluetoothViewModel Bluetooth { get; }
    public string? LogFilePath => _appLogService?.LogFilePath;
    public string VersionLabel { get; } = $"BUILD {AppBuildInfo.Display}";
    public string ProgressIdLabel { get; } =
        $"ID {ApplicationAssembly.ManifestModule.ModuleVersionId.ToString("N")[..8].ToUpperInvariant()}";
    public string BuildTimestampLabel { get; } = GetBuildTimestampLabel();

    private string _bluetoothStatusText = "Bluetooth: …";
    public string BluetoothStatusText
    {
        get => _bluetoothStatusText;
        private set
        {
            if (_bluetoothStatusText != value)
            {
                _bluetoothStatusText = value;
                OnPropertyChanged();
            }
        }
    }

    private static readonly System.Windows.Media.Brush BluetoothOnBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5E, 0xD3, 0x97));
    private static readonly System.Windows.Media.Brush BluetoothOffBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x9A, 0xA8));
    private System.Windows.Media.Brush _bluetoothStatusForeground = BluetoothOffBrush;
    public System.Windows.Media.Brush BluetoothStatusForeground
    {
        get => _bluetoothStatusForeground;
        private set
        {
            if (_bluetoothStatusForeground != value)
            {
                _bluetoothStatusForeground = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsControllerSection => CurrentSection == "Mandos";
    public bool IsSettingsSection => CurrentSection == "Ajustes";
    public bool IsTesterSection => CurrentSection == "Tester";
    public bool IsLogSection => CurrentSection == "Registro";
    public bool IsProfilesSection => CurrentSection == "Perfiles";
    public bool IsHidHideSection => CurrentSection == "HidHide";
    public bool IsBluetoothSection => CurrentSection == "Bluetooth";
    public bool ShowStatusCard =>
        !IsLogSection && !IsSettingsSection && !IsTesterSection && !IsProfilesSection &&
        !IsHidHideSection && !IsBluetoothSection;
    public bool HasConnectedGamepads => ConnectedGamepads.Count > 0;
    public bool ShowEmptyDevicesMessage =>
        IsControllerSection && _isControllerDataLoaded && ControllerItems.Count == 0;
    public bool ShowConnectedGamepads => IsControllerSection && ControllerItems.Count > 0;
    public bool IsControllerDataLoaded => _isControllerDataLoaded;
    public bool IsVirtualGamepadBusy =>
        Profiles.IsAnyVirtualGamepadBusy || Tester.IsStartingVirtualGamepad;

    public string VirtualGamepadBusyMessage
    {
        get
        {
            if (Tester.IsStartingVirtualGamepad)
            {
                return "Iniciando mando virtual desde Tester…";
            }

            VirtualGamepadControllerViewModel? busyItem = Profiles.VirtualGamepads
                .FirstOrDefault(item => item.IsStarting);
            return busyItem is null
                ? "Procesando mando virtual…"
                : $"Procesando «{busyItem.DisplayName}»…";
        }
    }
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                NotifyControllerCommandsCanExecuteChanged();
                OnPropertyChanged(nameof(RefreshButtonText));
            }
        }
    }

    public string RefreshButtonText => IsRefreshing ? "Actualizando..." : "Actualizar";

    public string CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(IsControllerSection));
                OnPropertyChanged(nameof(IsSettingsSection));
                OnPropertyChanged(nameof(IsTesterSection));
                OnPropertyChanged(nameof(IsLogSection));
                OnPropertyChanged(nameof(IsProfilesSection));
                OnPropertyChanged(nameof(IsHidHideSection));
                OnPropertyChanged(nameof(IsBluetoothSection));
                OnPropertyChanged(nameof(ShowStatusCard));
                OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
                OnPropertyChanged(nameof(ShowConnectedGamepads));
            }
        }
    }

    public string SectionTitle
    {
        get => _sectionTitle;
        private set => SetProperty(ref _sectionTitle, value);
    }

    public string SectionDescription
    {
        get => _sectionDescription;
        private set => SetProperty(ref _sectionDescription, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public MainWindowViewModel(
        IGamepadDetectionService? gamepadDetectionService = null,
        IAppLogService? appLogService = null,
        IControllerRepository? controllerRepository = null,
        Func<ControllerRecord, bool>? confirmRemoveSavedController = null,
        SettingsViewModel? settingsViewModel = null,
        IGamepadInputService? gamepadInputService = null,
        IProfileRepository? profileRepository = null,
        Func<ControllerProfile, bool>? confirmDeleteProfile = null,
        IVirtualGamepadService? virtualGamepadService = null,
        Func<ConnectedGamepad, bool>? requestElevatedVirtualGamepad = null,
        IHidHideService? hidHideService = null,
        Func<string>? defaultVirtualGamepadType = null,
        Func<Guid?>? defaultProfileId = null,
        IGlobalPresetRepository? globalPresetRepository = null,
        Action<string, string>? trayBalloon = null)
    {
        _gamepadDetectionService = gamepadDetectionService;
        _appLogService = appLogService;
        _controllerRepository = controllerRepository;
        _profileRepository = profileRepository;
        _confirmDeleteProfile = confirmDeleteProfile;
        _defaultProfileId = defaultProfileId;
        _confirmRemoveSavedController = confirmRemoveSavedController;
        _settingsViewModel = settingsViewModel;
        IGamepadInputService? inputService =
            gamepadInputService ?? gamepadDetectionService as IGamepadInputService;
        _testerViewModel = new TesterViewModel(
            inputService,
            message => _appLogService?.Write(
                DiagnosticLogLevel.Error,
                "Tester",
                message),
            virtualGamepadService,
            requestElevatedVirtualGamepad,
            defaultVirtualGamepadType);
        _hidHideService = hidHideService ?? new HidHideService();
        _toastService = new ToastNotificationService(
            () => System.Windows.Application.Current?.MainWindow?.IsVisible == true,
            () => NotificationModes.Normalize(_settingsViewModel?.NotificationMode),
            balloonFallback: trayBalloon,
            diagnosticLog: message => _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Notificaciones",
                message),
            queueIntervalMs: () => (int)Math.Round(
                (_settingsViewModel?.NotificationQueueIntervalSeconds ?? 3) * 1000),
            onToastActivated: () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (System.Windows.Application.Current?.MainWindow is System.Windows.Window window)
                    {
                        if (window.WindowState == System.Windows.WindowState.Minimized)
                        {
                            window.WindowState = System.Windows.WindowState.Normal;
                        }

                        window.Show();
                        window.Activate();
                    }
                })),
            isEventEnabled: eventId => Models.NotificationEvents.IsEnabled(
                _settingsViewModel?.NotificationEventStates, eventId));
        HidHide = new HidHideViewModel(
            _hidHideService,
            (level, message, exception) => _appLogService?.Write(
                level,
                "HidHide",
                message,
                exception));
        _dispatcher = Dispatcher.CurrentDispatcher;
        Profiles = new ProfilesViewModel(
            _profileRepository,
            _controllerRepository,
            UpdateControllerProfileAssignment,
            _confirmDeleteProfile,
            (message, exception) => _appLogService?.Write(
                DiagnosticLogLevel.Error,
                "Perfiles",
                message,
                exception),
            inputService,
            virtualGamepadService,
            requestElevatedVirtualGamepad,
            HidHide.HideAutomaticallyAsync,
            HidHide.ShowAutomaticallyAsync,
            device => _hidHideService.IsPhysicalDevicePresent(device.DevicePath!),
            (level, message, exception) => _appLogService?.Write(
                level,
                "AutoHide diagnóstico",
                message,
                exception),
            defaultVirtualGamepadType,
            new SonyControllerLedService(),
            new ControllerPowerOffService(),
            isDeviceHiddenByGdm: device =>
                !string.IsNullOrWhiteSpace(device.DevicePath) &&
                _hidHideService.IsHiddenByGdm(device.DevicePath),
            globalPresetRepository: globalPresetRepository,
            saveUnsavedController: SaveUnsavedControllerFromComboAsync,
            notifyEvent: (eventId, title, lines) => _toastService.ShowEvent(eventId, title, lines),
            batteryNotifyRepeatEnabled: () =>
                _settingsViewModel?.BatteryNotifyRepeatEnabled ?? true);
        Updates = new UpdateViewModel(
            new GitHubUpdateService(),
            () => UpdateChannels.Normalize(_settingsViewModel?.UpdateChannel),
            RequestUpdateInstallExit,
            _appLogService);
        var bluetoothService = new BluetoothDeviceService(
            message => _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Bluetooth",
                message));
        Bluetooth = new BluetoothViewModel(
            bluetoothService,
            _settingsViewModel,
            _dispatcher,
            (eventId, title, message) => _toastService.ShowEvent(eventId, title, message),
            message => _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Bluetooth",
                message));
        Bluetooth.Initialize();
        Profiles.PropertyChanged += OnProfilesPropertyChanged;
        Profiles.VirtualPollIntervalChanged += OnVirtualPollIntervalChanged;
        Profiles.LedColorChanged += OnLedColorChanged;
        NavigateCommand = new NavigationCommand(Navigate);
        _refreshDevicesCommand = new AsyncCommand(
            () => RefreshDevicesAsync(forceRescan: true),
            () => !IsRefreshing && !_isSavingController && !_isRemovingController);
        _saveControllerCommand = new AsyncParameterCommand(
            parameter => parameter is ControllerListItem item
                ? SaveControllerAsync(item)
                : Task.CompletedTask,
            parameter => parameter is ControllerListItem { CanSave: true } &&
                _controllerRepository is not null &&
                _gamepadDetectionService is not null &&
                _isControllerDataLoaded &&
                !IsRefreshing &&
                !_isSavingController &&
                !_isRemovingController);
        _removeSavedControllerCommand = new AsyncParameterCommand(
            parameter => parameter is ControllerListItem item
                ? RemoveSavedControllerAsync(item)
                : Task.CompletedTask,
            parameter => parameter is ControllerListItem { CanRemoveSaved: true } &&
                _controllerRepository is not null &&
                _isControllerDataLoaded &&
                !IsRefreshing &&
                !_isSavingController &&
                !_isRemovingController);
        if (_gamepadDetectionService is not null)
        {
            _pollTimer = new System.Threading.Timer(
                OnPollTimerElapsed,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _deviceEventTimer = new System.Threading.Timer(
                OnDeviceEventTimerElapsed,
                null,
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(200));
            _gamepadDetectionService.DeviceMembershipChanged += OnSdlDeviceMembershipChanged;
        }

        _testerInputTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _testerInputTimer.Tick += OnTesterInputTimerTick;
        _testerViewModel.PropertyChanged += OnTesterPropertyChanged;

        _idlePowerOffTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _idlePowerOffTimer.Tick += OnIdlePowerOffTimerTick;

        RefreshBluetoothStatus();
        _bluetoothStatusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _bluetoothStatusTimer.Tick += (_, _) => RefreshBluetoothStatus();
        _bluetoothStatusTimer.Start();

        ConnectedGamepads.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConnectedGamepads));
            OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
            OnPropertyChanged(nameof(ShowConnectedGamepads));
            HidHide.UpdateDevices(GetAllHidHideDevices(ConnectedGamepads));
        };
        if (_settingsViewModel is not null)
        {
            _settingsViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.ShowVirtualGamepadsInAllTabs))
                {
                    HidHide.UpdateDevices(GetAllHidHideDevices(ConnectedGamepads));
                    RebuildControllerItems();
                }
                if (args.PropertyName == nameof(SettingsViewModel.VirtualInputPollIntervalMs) &&
                    Profiles.IsAnyVirtualGamepadRunning)
                {
                    _testerInputTimer.Interval = EffectiveVirtualInputPollInterval;
                }
            };
        }
        SavedControllers.CollectionChanged += (_, _) =>
        {
            RebuildControllerItems();
            Profiles.UpdateSavedControllers(SavedControllers);
        };

        if (_appLogService is not null)
        {
            foreach (DiagnosticLogEntry entry in _appLogService.GetRecentEntries())
            {
                LogEntries.Add(entry);
            }

            _appLogService.EntryAdded += OnLogEntryAdded;
        }
    }

    public async Task InitializeAsync()
    {
        if (!_isControllerDataLoaded)
        {
            if (_controllerInitializationTask is null || _controllerInitializationTask.IsCompleted)
            {
                _controllerInitializationTask = LoadSavedControllersAsync();
            }

            await _controllerInitializationTask;
        }

        if (Profiles.HasAutoStartVirtualGamepad && !_hasInitializedAutoStart)
        {
            _hasInitializedAutoStart = true;
            _hasInitializedDeviceDetection = true;
            StartMonitoring();
            await Profiles.InitializeAsync();
            await RefreshDevicesAsync(forceRescan: true);
        }

        if (!_hasInitializedDeviceDetection)
        {
            _hasInitializedDeviceDetection = true;
            StartMonitoring();
            await RefreshDevicesAsync(forceRescan: true);
        }
    }

    public async Task HandleSettingsAppliedAsync(AppSettings previous, AppSettings current)
    {
        if (string.Equals(
                previous.DefaultVirtualGamepadType,
                current.DefaultVirtualGamepadType,
                StringComparison.Ordinal) &&
            string.Equals(
                previous.Xbox360ProfileVariant,
                current.Xbox360ProfileVariant,
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await Profiles.RestartVirtualGamepadsForDefaultTypeAsync();
        }
        catch (Exception exception)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Error,
                "Mando virtual",
                "No se pudieron reiniciar las salidas virtuales tras cambiar el tipo predeterminado.",
                exception);
        }
    }

    private void KickoffStartupUpdateCheck()
    {
        if (_startupUpdateCheckStarted ||
            _settingsViewModel?.CheckForUpdatesOnStartup != true)
        {
            return;
        }

        _startupUpdateCheckStarted = true;
        _ = CheckForUpdatesOnStartupAsync();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Updates.CheckAsync(silent: true);
            if (Updates.HasUpdateAvailable)
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Information,
                    "Actualizaciones",
                    "Se detectó una build nueva de GDM en GitHub.");
                await PromptUpdateNotificationAsync();
            }
        }
        catch (Exception exception)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Actualizaciones",
                "Error en la verificación automática de actualizaciones.",
                exception);
        }
    }

    private async Task PromptUpdateNotificationAsync()
    {
        if (_settingsViewModel?.SkipUpdateNotifications == true)
        {
            return;
        }

        AppReleaseInfo? release = Updates.AvailableUpdate;
        if (release is null)
        {
            return;
        }

        (bool installRequested, bool dontShowAgain) = UpdateAvailableWindow.ShowPrompt(
            System.Windows.Application.Current?.MainWindow,
            release.Version,
            release.TagName);

        if (dontShowAgain && _settingsViewModel is not null)
        {
            try
            {
                await _settingsViewModel.SetSkipUpdateNotificationsAsync(true);
            }
            catch (Exception exception)
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Warning,
                    "Actualizaciones",
                    "No se pudo guardar la preferencia de avisos de actualización.",
                    exception);
            }
        }

        if (installRequested)
        {
            await Updates.InstallAvailableUpdateAsync();
        }
    }
    private void RequestUpdateInstallExit()
    {
        if (System.Windows.Application.Current is App app)
        {
            app.RequestProgrammaticExit(skipExitConfirmation: true);
        }
        else
        {
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private async Task LoadSavedControllersAsync()
    {
        if (_controllerRepository is null)
        {
            _isControllerDataLoaded = true;
            KickoffStartupUpdateCheck();
            OnPropertyChanged(nameof(IsControllerDataLoaded));
            OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
            _refreshDevicesCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            IReadOnlyList<ControllerRecord> records = await _controllerRepository.LoadAllAsync();
            SavedControllers.Clear();
            foreach (ControllerRecord record in records)
            {
                SavedControllers.Add(record);
            }

            _isControllerDataLoaded = true;
            KickoffStartupUpdateCheck();
            OnPropertyChanged(nameof(IsControllerDataLoaded));
            OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
            RebuildControllerItems();
            _saveControllerCommand.NotifyCanExecuteChanged();
            _removeSavedControllerCommand.NotifyCanExecuteChanged();
            _refreshDevicesCommand.NotifyCanExecuteChanged();
        }
        catch (InvalidDataException exception)
        {
            SetControllerLoadError("Los mandos guardados no se pudieron validar", exception);
            _controllerInitializationTask = null;
        }
        catch (IOException exception)
        {
            SetControllerLoadError("No se pudieron leer los mandos guardados", exception);
            _controllerInitializationTask = null;
        }
        catch (UnauthorizedAccessException exception)
        {
            SetControllerLoadError("No hay permisos para leer los mandos guardados", exception);
            _controllerInitializationTask = null;
        }
    }

    public async Task RefreshDevicesAsync(bool forceRescan = false)
    {
        if (_controllerRepository is not null && !_isControllerDataLoaded)
        {
            await InitializeAsync();
            if (!_isControllerDataLoaded)
            {
                return;
            }
        }

        if (_gamepadDetectionService is null || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;

        try
        {
            StatusTitle = "Buscando mandos conectados";
            StatusMessage = forceRescan
                ? "Actualizando la lista de dispositivos SDL2 sin reiniciar el subsistema."
                : "Consultando SDL2. Los dispositivos emparejados o cercanos no se incluyen en esta lista.";

            if (forceRescan)
            {
                await _gamepadDetectionService.RefreshAsync(cancellationToken);
            }
            await _gamepadDetectionService.InitializeAsync(cancellationToken);
            IReadOnlyList<ConnectedGamepad> devices =
                await _gamepadDetectionService.EnumerateConnectedAsync(cancellationToken);

            _ = HideRedundantInterfacesAsync(cancellationToken);
            HidHide.UpdateDevices(GetAllHidHideDevices(devices));
            ConnectedGamepad[] displayDevices = MergeHiddenDevices(devices);
            bool devicesChanged = !ConnectedGamepads.SequenceEqual(displayDevices);
            if (devicesChanged)
            {
                ConnectedGamepads.Clear();
                foreach (ConnectedGamepad device in displayDevices)
                {
                    ConnectedGamepads.Add(device);
                }
            }
            Tester.UpdateDevices(displayDevices);
            if (devicesChanged)
            {
                Profiles.PruneStaleDeviceState(
                    displayDevices.Select(device => device.DeviceKey).ToArray());
                RebuildControllerItems();
            }
            // Después de reconstruir items: la notificación de conexión
            // necesita el SavedRecord ya vinculado para distinguir mandos
            // guardados de no guardados.
            NotifyControllerPresenceChanges(displayDevices);

            int hiddenCount = displayDevices.Count(device => device.IsHiddenByGdm);
            StatusTitle = devices.Count == 0
                ? hiddenCount == 0
                    ? "SDL2 no ve mandos accesibles"
                    : $"{hiddenCount} mando{(hiddenCount == 1 ? string.Empty : "s")} oculto{(hiddenCount == 1 ? string.Empty : "s")} por HidHide"
                : $"{devices.Count} mando{(devices.Count == 1 ? string.Empty : "s")} detectado{(devices.Count == 1 ? string.Empty : "s")}" +
                  (hiddenCount > 0
                      ? $" · {hiddenCount} oculto{(hiddenCount == 1 ? string.Empty : "s")}"
                      : string.Empty);
            StatusMessage = hiddenCount > 0
                ? "Los mandos ocultos por GDM se mantienen en las listas mientras Windows confirme que siguen conectados. SDL puede no permitir leer sus entradas mientras estén ocultos."
                : devices.Count == 0
                    ? "SDL2 no detecta mandos accesibles. Si usas DS4Windows o HidHide, el mando físico puede estar oculto para GDM. Añade GDM a la lista de aplicaciones permitidas de HidHide; no hace falta desactivar la protección global."
                    : "La lista incluye dispositivos detectados por SDL2 y conserva los mandos ocultos por GDM mientras sigan conectados.";

            if (_lastLoggedDevices is null || !_lastLoggedDevices.SequenceEqual(devices))
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Information,
                    "Mandos",
                    FormatDeviceChange(devices));
                _lastLoggedDevices = devices.ToArray();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (DllNotFoundException exception)
        {
            SetDetectionError("No se encontró SDL2", exception);
        }
        catch (BadImageFormatException exception)
        {
            SetDetectionError("La versión de SDL2 no es compatible", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            SetDetectionError("Falta una función requerida de SDL2", exception);
        }
        catch (ObjectDisposedException exception)
        {
            SetDetectionError("El servicio SDL2 ya está cerrado", exception);
        }
        catch (InvalidOperationException exception)
        {
            SetDetectionError("SDL2 no pudo consultar los mandos", exception);
        }
        catch (Exception exception)
        {
            SetDetectionError("Error inesperado al consultar los mandos", exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public async Task StartVirtualGamepadOnStartupAsync(ConnectedGamepad requestedDevice)
    {
        ArgumentNullException.ThrowIfNull(requestedDevice);
        await InitializeAsync();
        await InitializeAndRefreshAsync();
        await Profiles.InitializeAsync();
        while (IsRefreshing)
        {
            await Task.Delay(50);
        }

        ConnectedGamepad? matchedDevice = FindStartupDevice(requestedDevice);
        if (matchedDevice is null)
        {
            Profiles.ReportVirtualGamepadStatus(
                "GDM se abrió como administrador, pero no pudo identificar sin ambigüedad el mando original. " +
                "Comprueba que el mando físico guardado esté conectado e inténtalo de nuevo.");
            return;
        }

        Profiles.UpdateConnectedControllers(GetProfilesVisibleControllerItems());
        Profiles.UpdateAllConnectedDevices(ConnectedGamepads);
        await Profiles.StartVirtualGamepadOnStartupAsync(matchedDevice);
    }

    public async Task CompleteStartupAsync()
    {
        await Profiles.InitializeAsync();

        await Profiles.CompleteStartupAsync();
    }

    private ConnectedGamepad? FindStartupDevice(ConnectedGamepad requestedDevice)
    {
        if (!string.IsNullOrWhiteSpace(requestedDevice.DevicePath))
        {
            ConnectedGamepad[] pathMatches = ConnectedGamepads
                .Where(device => string.Equals(
                    device.DevicePath,
                    requestedDevice.DevicePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (pathMatches.Length == 1)
            {
                return pathMatches[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedDevice.SerialNumber))
        {
            ConnectedGamepad[] serialMatches = ConnectedGamepads
                .Where(device =>
                    string.Equals(
                        device.SerialNumber,
                        requestedDevice.SerialNumber,
                        StringComparison.OrdinalIgnoreCase) &&
                    device.VendorId == requestedDevice.VendorId &&
                    device.ProductId == requestedDevice.ProductId)
                .ToArray();
            if (serialMatches.Length == 1)
            {
                return serialMatches[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(requestedDevice.SdlGuid) &&
            requestedDevice.VendorId is not null &&
            requestedDevice.ProductId is not null)
        {
            ConnectedGamepad[] guidMatches = ConnectedGamepads
                .Where(device =>
                    string.Equals(
                        device.SdlGuid,
                        requestedDevice.SdlGuid,
                        StringComparison.OrdinalIgnoreCase) &&
                    device.VendorId == requestedDevice.VendorId &&
                    device.ProductId == requestedDevice.ProductId &&
                    string.Equals(device.Name, requestedDevice.Name, StringComparison.Ordinal))
                .ToArray();
            if (guidMatches.Length == 1)
            {
                return guidMatches[0];
            }
        }

        return null;
    }

    public void StartMonitoring()
    {
        if (_gamepadDetectionService is null || _isMonitoring)
        {
            return;
        }

        _isMonitoring = true;
        _idlePowerOffTimer.Start();
        _testerInputTimer.Start();
        UpdateDevicePollingInterval();
    }

    public void StopMonitoring()
    {
        _pollTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _idlePowerOffTimer.Stop();
        _bluetoothStatusTimer.Stop();
        _testerInputTimer.Stop();
        _isMonitoring = false;
        _refreshCancellation?.Cancel();
    }

    private async void OnIdlePowerOffTimerTick(object? sender, EventArgs e)
    {
        if (!_isMonitoring)
        {
            return;
        }

        try
        {
            await Profiles.PollControllerIdlePowerOffAsync();
        }
        catch (Exception exception)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Apagado por inactividad",
                "Error en el sondeo de apagado por inactividad.",
                exception);
        }
    }

    private void UpdateDevicePollingInterval()
    {
        if (_isMonitoring)
        {
            TimeSpan interval = Profiles.IsAnyVirtualGamepadRunning
                ? VirtualOutputDevicePollingInterval
                : NormalDevicePollingInterval;
            _pollTimer?.Change(interval, interval);
        }
    }

    private void OnPollTimerElapsed(object? state)
    {
        if (Interlocked.Exchange(ref _isPollQueued, 1) != 0)
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _isPollQueued, 0);
            return;
        }

        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (_isMonitoring &&
                    (_controllerRepository is null || _isControllerDataLoaded))
                {
                    bool hiddenIdle = IsAppWindowHidden && !Profiles.IsAnyVirtualGamepadRunning;
                    long nowMs = Environment.TickCount64;
                    if (!hiddenIdle ||
                        nowMs - _lastHiddenDeviceRefreshMs >= HiddenIdleDeviceRefreshMs)
                    {
                        if (hiddenIdle)
                        {
                            _lastHiddenDeviceRefreshMs = nowMs;
                        }

                        await RefreshDevicesAsync();
                    }
                }
            }
            catch (Exception exception)
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Error,
                    "Mandos",
                    "Error inesperado al refrescar los dispositivos.",
                    exception);
            }
            finally
            {
                Interlocked.Exchange(ref _isPollQueued, 0);
            }
        }));
    }

    private void OnDeviceEventTimerElapsed(object? state)
    {
        if (!_isMonitoring ||
            _dispatcher.HasShutdownStarted ||
            _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _gamepadDetectionService?.PumpDeviceEvents();
            }
            catch
            {
            }
        }));
    }

    private void OnSdlDeviceMembershipChanged()
    {
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (_isMonitoring)
                {
                    await RefreshDevicesAsync();
                }
            }
            catch (Exception exception)
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Error,
                    "Mandos",
                    "Error inesperado al procesar el cambio de dispositivos.",
                    exception);
            }
        });
    }

    private bool IsAppWindowHidden
    {
        get
        {
            Window? window = System.Windows.Application.Current?.MainWindow;
            return window is null || !window.IsVisible ||
                window.WindowState == WindowState.Minimized;
        }
    }

    private async void OnTesterInputTimerTick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _isTesterTickRunning, 1) != 0)
        {
            return;
        }

        bool windowHidden = IsAppWindowHidden;

        try
        {
            await Profiles.ReadVirtualGamepadInputsAsync();
            if (!windowHidden &&
                (IsTesterSection || Tester.IsVirtualGamepadRunning))
            {
                Tester.MotionTestActive = IsTesterSection;
                await Tester.ReadSelectedInputAsync();
            }
        }
        catch (DllNotFoundException exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (BadImageFormatException exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (ObjectDisposedException exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (InvalidOperationException exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (IOException exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            await HandleTesterInputErrorAsync(exception);
        }
        catch (Exception exception)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Error,
                "Sondeo de entrada",
                "Error inesperado en el sondeo de entrada del tester.",
                exception);
        }
        finally
        {
            Interlocked.Exchange(ref _isTesterTickRunning, 0);
        }
    }

    private void OnTesterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TesterViewModel.IsStartingVirtualGamepad) ||
            e.PropertyName == nameof(TesterViewModel.IsVirtualGamepadRunning))
        {
            OnPropertyChanged(nameof(IsVirtualGamepadBusy));
            OnPropertyChanged(nameof(VirtualGamepadBusyMessage));
        }

        if (e.PropertyName == nameof(TesterViewModel.IsVirtualGamepadRunning))
        {
            if (Tester.IsVirtualGamepadRunning)
            {
                _testerInputTimer.Interval = EffectiveVirtualInputPollInterval;
                _testerInputTimer.Start();
            }
            else
            {
                _testerInputTimer.Interval = TimeSpan.FromMilliseconds(50);
            }
        }
    }

    private void OnProfilesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilesViewModel.HasAutoStartVirtualGamepad))
        {
            if (Profiles.HasAutoStartVirtualGamepad && _isControllerDataLoaded)
            {
                StartMonitoring();
                _ = InitializeAndRefreshAsync();
            }
            return;
        }

        if (e.PropertyName == nameof(ProfilesViewModel.IsAnyVirtualGamepadBusy))
        {
            OnPropertyChanged(nameof(IsVirtualGamepadBusy));
            OnPropertyChanged(nameof(VirtualGamepadBusyMessage));
            return;
        }

        if (e.PropertyName != nameof(ProfilesViewModel.IsAnyVirtualGamepadRunning))
        {
            return;
        }

        UpdateDevicePollingInterval();
        if (Profiles.IsAnyVirtualGamepadRunning)
        {
            _testerInputTimer.Interval = EffectiveVirtualInputPollInterval;
            _testerInputTimer.Start();
        }
        else
        {
            _testerInputTimer.Interval = TimeSpan.FromMilliseconds(50);
        }
    }

    private Task HandleTesterInputErrorAsync(Exception exception)
    {
        Tester.ReportInputError(exception);
        return Task.CompletedTask;
    }

    private void Navigate(object? parameter)
    {
        if (parameter is not string section)
        {
            return;
        }

        CurrentSection = section;

        (SectionTitle, SectionDescription, StatusTitle, StatusMessage) = section switch
        {
            "Mandos" =>
            (
                "Mandos",
                "Mandos conectados detectados por SDL2.",
                _gamepadDetectionService is null ? "Detección no configurada" : "Listo para detectar",
                _gamepadDetectionService is null
                    ? "El servicio de detección SDL2 no está disponible."
                    : "Pulsa Actualizar para consultar los mandos conectados y accesibles a GDM."
            ),
            "Ajustes" =>
            (
                "Ajustes",
                "Preferencias generales de GDM.",
                "Ajustes disponibles",
                "Personaliza la apariencia de la aplicación y guarda tus preferencias."
            ),
            "Tester" =>
            (
                "Tester",
                "Comprueba en tiempo real los controles de los mandos conectados.",
                "Tester de mandos",
                "Selecciona un mando conectado para ver sus botones y ejes. La lectura no modifica la configuración."
            ),
            "Perfiles" =>
            (
                "Perfiles",
                "Crea perfiles reutilizables y asígnalos a tus mandos guardados.",
                "Perfiles de mando",
                "Los perfiles se guardan en archivos JSON y todavía no aplican remapeos a los juegos."
            ),
            "HidHide" =>
            (
                "HidHide",
                "Oculta mandos físicos para otras aplicaciones sin perder acceso desde GDM.",
                "Control de ocultamiento",
                "Al cerrar GDM los mandos ocultados se restauran solo si la opción de Ajustes > Mandos está activada; tras un cierre inesperado se recuperan al siguiente inicio."
            ),
            "Bluetooth" =>
            (
                "Bluetooth",
                "Busca y empareja mandos y dispositivos Bluetooth.",
                "Dispositivos Bluetooth",
                "Activa la auto-conexión para que GDM empareje mandos automáticamente al detectarlos, o empareja manualmente desde la lista."
            ),
            "Registro" =>
            (
                "Registro",
                "Diagnóstico de errores y eventos de GDM durante la ejecución.",
                "Registro de la aplicación",
                "Los errores de ejecución y las operaciones relevantes se muestran aquí y se guardan en el archivo local."
            ),
            _ =>
            (
                "Inicio",
                "Base de la nueva versión de Gamepad Device Manager.",
                "La arquitectura inicial está lista",
                "La aplicación está organizada por categorías. La detección y configuración de mandos se implementarán en fases posteriores."
            )
        };

        if (CurrentSection is "Mandos" or "Tester" or "Perfiles" or "HidHide")
        {
            StartMonitoring();
            if (Profiles.IsAnyVirtualGamepadRunning)
            {
                _testerInputTimer.Interval = EffectiveVirtualInputPollInterval;
            }
            else
            {
                _testerInputTimer.Interval = TimeSpan.FromMilliseconds(50);
            }

            _ = InitializeAndRefreshAsync();
            if (CurrentSection == "Perfiles")
            {
                _ = InitializeProfilesAsync();
            }
            if (CurrentSection == "HidHide")
            {
                _ = HidHide.RefreshStatusAsync();
            }
        }
        else
        {
            StartMonitoring();
            if (Profiles.HasAutoStartVirtualGamepad)
            {
                _ = InitializeAndRefreshAsync();
            }
        }
    }

    private async Task InitializeProfilesAsync()
    {
        await InitializeAsync();
        if (_isControllerDataLoaded)
        {
            Profiles.UpdateSavedControllers(SavedControllers);
        }
        else
        {
            Profiles.SetControllersUnavailable();
        }

        await Profiles.InitializeAsync();
    }

    private void UpdateControllerProfileAssignment(ControllerRecord updated)
    {
        for (int index = 0; index < SavedControllers.Count; index++)
        {
            if (SavedControllers[index].ControllerId == updated.ControllerId)
            {
                SavedControllers[index] = updated;
                return;
            }
        }
    }

    private async Task InitializeAndRefreshAsync()
    {
        await InitializeAsync();
        if (_controllerRepository is not null && !_isControllerDataLoaded)
        {
            StopMonitoring();
            return;
        }

        await RefreshDevicesAsync(forceRescan: true);
    }


    public async Task<bool> SaveControllerAsync(ControllerListItem item)
    {
        if (item.ConnectedDevice is not { IsVirtualGamepad: false } device ||
            _controllerRepository is null ||
            !_isControllerDataLoaded ||
            _gamepadDetectionService is null ||
            _isSavingController ||
            _isRemovingController ||
            IsRefreshing)
        {
            return false;
        }

        _isSavingController = true;
        NotifyControllerCommandsCanExecuteChanged();
        try
        {
            await _gamepadDetectionService.InitializeAsync();
            await _gamepadDetectionService.RefreshAsync();
            IReadOnlyList<ConnectedGamepad> currentDevices =
                await _gamepadDetectionService.EnumerateConnectedAsync();
            ConnectedGamepad? currentDevice = currentDevices.SingleOrDefault(
                item => item.DeviceKey == device.DeviceKey);
            if (currentDevice is null ||
                currentDevice.VendorId != device.VendorId ||
                currentDevice.ProductId != device.ProductId ||
                !string.Equals(currentDevice.SerialNumber, device.SerialNumber, StringComparison.Ordinal))
            {
                StatusTitle = "El mando ya no está conectado";
                StatusMessage = "Actualiza la lista y vuelve a intentar guardar el mando.";
                return false;
            }

            if (HasSavedSerial(currentDevice))
            {
                StatusTitle = "El mando ya está guardado";
                StatusMessage = "GDM ya tiene un registro con el identificador individual de este mando.";
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var record = new ControllerRecord
            {
                ControllerId = Guid.NewGuid(),
                DisplayName = currentDevice.Name,
                Identity = new ControllerIdentity
                {
                    Model = currentDevice.Name,
                    Evidence = CreateIdentityEvidence(currentDevice)
                },
                ProfileId = ControllerProfile.GlobalPresetId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _controllerRepository.SaveAsync(record);
            SavedControllers.Add(record);
            if (string.IsNullOrWhiteSpace(currentDevice.SerialNumber))
            {
                _sessionSavedDeviceKeys[record.ControllerId] = currentDevice.DeviceKey;
            }
            RebuildControllerItems();
            StatusTitle = "Mando guardado";
            StatusMessage = string.IsNullOrWhiteSpace(currentDevice.SerialNumber)
                ? "SDL2 no proporcionó un número de serie. Al reconectar, GDM asociará el registro solo si nombre y VID/PID coinciden de forma única; esto identifica el modelo, no garantiza que sea la misma unidad física."
                : "El registro del mando se guardó. GDM lo asociará al reconectarlo solo si el identificador individual coincide de forma única.";
            if (record.ProfileId is Guid assignedProfileId &&
                Profiles.Profiles.FirstOrDefault(profile => profile.ProfileId == assignedProfileId) is { } assignedProfile)
            {
                StatusMessage += $" Se asignó el perfil «{assignedProfile.DisplayName}».";
            }
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Mandos",
                $"Se guardó el mando «{record.DisplayName}».");
            return true;
        }
        catch (InvalidDataException exception)
        {
            SetControllerSaveError("No se pudo validar el registro del mando", exception);
        }
        catch (IOException exception)
        {
            SetControllerSaveError("No se pudo escribir el registro del mando", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            SetControllerSaveError("No hay permisos para guardar el mando", exception);
        }
        catch (InvalidOperationException exception)
        {
            SetControllerSaveError("No se pudo consultar el mando antes de guardarlo", exception);
        }
        catch (DllNotFoundException exception)
        {
            SetControllerSaveError("No se encontró SDL2 al guardar el mando", exception);
        }
        catch (BadImageFormatException exception)
        {
            SetControllerSaveError("SDL2 no es compatible al guardar el mando", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            SetControllerSaveError("Falta una función requerida de SDL2", exception);
        }
        finally
        {
            _isSavingController = false;
            NotifyControllerCommandsCanExecuteChanged();
        }

        return false;
    }

    /// <summary>
    /// Guarda un mando físico no registrado usando exactamente el mismo flujo
    /// que el botón «Guardar» de la pestaña Mandos. Lo invoca el combo global
    /// «guardar mando nuevo» configurado en Ajuste global → Mando virtual.
    /// </summary>
    private async Task<bool> SaveUnsavedControllerFromComboAsync(ConnectedGamepad device)
    {
        var item = new ControllerListItem(device, null);
        return await SaveControllerAsync(item);
    }

    public async Task RemoveSavedControllerAsync(ControllerListItem item)
    {
        if (item.SavedRecord is not { } record ||
            _controllerRepository is null ||
            !_isControllerDataLoaded ||
            _isRemovingController ||
            IsRefreshing ||
            _confirmRemoveSavedController?.Invoke(record) != true)
        {
            return;
        }

        _isRemovingController = true;
        NotifyControllerCommandsCanExecuteChanged();
        try
        {
            // Antes de eliminar: detener la salida virtual del mando (si está
            // activa) y mostrar el físico si GDM lo tiene oculto, para no
            // dejarlo oculto sin configuración que lo gestione.
            await Profiles.StopVirtualOutputForControllerAsync(record.ControllerId);
            await _controllerRepository.DeleteAsync(record.ControllerId);
            SavedControllers.Remove(record);
            _sessionSavedDeviceKeys.Remove(record.ControllerId);
            StatusTitle = "Mando eliminado de guardados";
            StatusMessage = "Se eliminó su configuración de GDM. El emparejamiento de Windows no se modificó.";
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Mandos",
                $"Se eliminó de guardados el mando «{record.DisplayName}».");
        }
        catch (InvalidDataException exception)
        {
            SetControllerSaveError("No se pudo validar el catálogo de mandos", exception);
        }
        catch (IOException exception)
        {
            SetControllerSaveError("No se pudo eliminar el registro del mando", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            SetControllerSaveError("No hay permisos para eliminar el mando guardado", exception);
        }
        finally
        {
            _isRemovingController = false;
            NotifyControllerCommandsCanExecuteChanged();
        }
    }

    private bool HasSavedSerial(ConnectedGamepad device)
    {
        if (string.IsNullOrWhiteSpace(device.SerialNumber))
        {
            return false;
        }

        return SavedControllers.Any(record =>
            string.Equals(
                GetSavedSerial(record),
                device.SerialNumber,
                StringComparison.Ordinal) &&
            HasMatchingHardwareIds(record, device));
    }

    private void RebuildControllerItems()
    {
        if (!_isControllerDataLoaded)
        {
            return;
        }

        ControllerItems.Clear();
        var matchedDeviceKeys = new HashSet<string>(StringComparer.Ordinal);
        var matchesByControllerId = new Dictionary<Guid, ConnectedGamepad>();

        foreach (ControllerRecord record in SavedControllers)
        {
            ConnectedGamepad? matchingDevice = FindInterfaceIdMatch(record, matchedDeviceKeys);
            if (matchingDevice is not null)
            {
                matchedDeviceKeys.Add(matchingDevice.DeviceKey);
                matchesByControllerId[record.ControllerId] = matchingDevice;
            }
        }

        foreach (ControllerRecord record in SavedControllers)
        {
            if (matchesByControllerId.ContainsKey(record.ControllerId))
            {
                continue;
            }

            ConnectedGamepad? matchingDevice = FindUniqueSerialMatch(record);
            if (matchingDevice is not null)
            {
                matchedDeviceKeys.Add(matchingDevice.DeviceKey);
                matchesByControllerId[record.ControllerId] = matchingDevice;
            }
        }

        foreach (ControllerRecord record in SavedControllers)
        {
            if (matchesByControllerId.ContainsKey(record.ControllerId))
            {
                continue;
            }

            ConnectedGamepad? matchingDevice = FindSessionDeviceMatch(record, matchedDeviceKeys);
            if (matchingDevice is not null)
            {
                matchedDeviceKeys.Add(matchingDevice.DeviceKey);
                matchesByControllerId[record.ControllerId] = matchingDevice;
            }
        }

        foreach (ControllerRecord record in SavedControllers)
        {
            if (matchesByControllerId.ContainsKey(record.ControllerId))
            {
                continue;
            }

            ConnectedGamepad? matchingDevice = FindUniqueModelMatch(record, matchedDeviceKeys);
            if (matchingDevice is not null)
            {
                matchedDeviceKeys.Add(matchingDevice.DeviceKey);
                matchesByControllerId[record.ControllerId] = matchingDevice;
            }
        }

        foreach (ControllerRecord record in SavedControllers)
        {
            matchesByControllerId.TryGetValue(record.ControllerId, out ConnectedGamepad? matchingDevice);
            ControllerItems.Add(WithCurrentLedColor(new ControllerListItem(matchingDevice, record)));
        }

        foreach (ConnectedGamepad device in ConnectedGamepads)
        {
            if (!matchedDeviceKeys.Contains(device.DeviceKey))
            {
                ControllerItems.Add(WithCurrentLedColor(new ControllerListItem(device, null)));
            }
        }

        Profiles.UpdateConnectedControllers(GetProfilesVisibleControllerItems());
        Profiles.UpdateAllConnectedDevices(ConnectedGamepads);
        OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
        OnPropertyChanged(nameof(ShowConnectedGamepads));
        _saveControllerCommand.NotifyCanExecuteChanged();
        _removeSavedControllerCommand.NotifyCanExecuteChanged();
    }

    private bool ShowVirtualGamepadsInHiddenTabs =>
        _settingsViewModel?.ShowVirtualGamepadsInAllTabs == true;

    private IReadOnlyList<ConnectedGamepad> GetHidHideVisibleDevices(
        IEnumerable<ConnectedGamepad> devices)
    {
        return ShowVirtualGamepadsInHiddenTabs
            ? devices.ToArray()
            : devices.Where(device => !device.IsVirtualGamepad).ToArray();
    }

    private IReadOnlyList<ConnectedGamepad> GetAllHidHideDevices(
        IEnumerable<ConnectedGamepad> devices)
    {
        var all = GetHidHideVisibleDevices(devices).ToList();
        if (_gamepadDetectionService is not null)
        {
            var knownKeys = all.Select(device => device.DeviceKey)
                .ToHashSet(StringComparer.Ordinal);
            all.AddRange(_gamepadDetectionService.RedundantInterfaces
                .Where(device => !knownKeys.Contains(device.DeviceKey)));
        }

        return all;
    }

    private ControllerListItem WithCurrentLedColor(ControllerListItem item)
    {
        if (item.ConnectedDevice?.DeviceKey is { } deviceKey &&
            Profiles.TryGetLedColor(deviceKey, out byte red, out byte green, out byte blue))
        {
            return item with
            {
                LedColorBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(red, green, blue))
            };
        }

        return item;
    }

    private void OnLedColorChanged(string deviceKey, byte red, byte green, byte blue)
    {
        _dispatcher.BeginInvoke(() =>
        {
            for (int index = 0; index < ControllerItems.Count; index++)
            {
                if (ControllerItems[index].ConnectedDevice?.DeviceKey == deviceKey)
                {
                    ControllerItems[index] = WithCurrentLedColor(ControllerItems[index]);
                }
            }
        });
    }

    private IEnumerable<ControllerListItem> GetProfilesVisibleControllerItems()
    {
        return ShowVirtualGamepadsInHiddenTabs
            ? ControllerItems
            : ControllerItems.Where(item => item.ConnectedDevice?.IsVirtualGamepad != true);
    }

    private ConnectedGamepad[] MergeHiddenDevices(IReadOnlyList<ConnectedGamepad> sdlDevices)
    {
        var merged = sdlDevices
            .Select(device => device with
            {
                IsHiddenByGdm = !string.IsNullOrWhiteSpace(device.DevicePath) &&
                    _hidHideService.IsHiddenByGdm(device.DevicePath)
            })
            .ToList();
        var knownDeviceKeys = merged
            .Select(device => device.DeviceKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (ConnectedGamepad hiddenDevice in HidHide.GetHiddenDevices())
        {
            if (knownDeviceKeys.Contains(hiddenDevice.DeviceKey))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(hiddenDevice.DevicePath))
            {
                continue;
            }

            if (merged.Any(device => string.Equals(
                    device.DevicePath,
                    hiddenDevice.DevicePath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            bool isPresent;
            try
            {
                isPresent = _hidHideService.IsPhysicalDevicePresent(hiddenDevice.DevicePath);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Warning,
                    "HidHide",
                    $"No se pudo confirmar la presencia de «{hiddenDevice.Name}» para mantenerlo en las listas.",
                    exception);
                continue;
            }

            if (isPresent)
            {
                merged.Add(hiddenDevice with { IsHiddenByGdm = true });
                knownDeviceKeys.Add(hiddenDevice.DeviceKey);
            }
        }

        return merged.ToArray();
    }

    private ConnectedGamepad? FindInterfaceIdMatch(
        ControllerRecord record,
        IReadOnlySet<string> matchedDeviceKeys)
    {
        string? interfaceId = GetSavedEvidence(record, "DeviceInterfaceId");
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return null;
        }

        ConnectedGamepad[] matches = ConnectedGamepads
            .Where(device =>
                !device.IsVirtualGamepad &&
                !matchedDeviceKeys.Contains(device.DeviceKey) &&
                string.Equals(
                    GetDeviceInterfaceId(device),
                    interfaceId,
                    StringComparison.Ordinal) &&
                HasMatchingHardwareIds(record, device))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private ConnectedGamepad? FindUniqueSerialMatch(ControllerRecord record)
    {
        string? serial = GetSavedSerial(record);
        if (string.IsNullOrWhiteSpace(serial) ||
            SavedControllers.Count(item =>
                string.Equals(GetSavedSerial(item), serial, StringComparison.Ordinal)) != 1)
        {
            return null;
        }

        ConnectedGamepad[] matches = ConnectedGamepads
            .Where(device =>
                !device.IsVirtualGamepad &&
                string.Equals(device.SerialNumber, serial, StringComparison.Ordinal) &&
                HasMatchingHardwareIds(record, device))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private ConnectedGamepad? FindSessionDeviceMatch(
        ControllerRecord record,
        IReadOnlySet<string> matchedDeviceKeys)
    {
        if (!string.IsNullOrWhiteSpace(GetSavedSerial(record)) ||
            !_sessionSavedDeviceKeys.TryGetValue(record.ControllerId, out string? deviceKey))
        {
            return null;
        }

        ConnectedGamepad? match = ConnectedGamepads.SingleOrDefault(
            device => device.DeviceKey == deviceKey &&
                !device.IsVirtualGamepad &&
                !matchedDeviceKeys.Contains(device.DeviceKey));
        if (match is null)
        {
            _sessionSavedDeviceKeys.Remove(record.ControllerId);
            return null;
        }

        return HasMatchingHardwareIds(record, match) ? match : null;
    }

    private ConnectedGamepad? FindUniqueModelMatch(
        ControllerRecord record,
        IReadOnlySet<string> matchedDeviceKeys)
    {
        string? vendor = GetSavedEvidence(record, "VendorId");
        string? product = GetSavedEvidence(record, "ProductId");
        string? model = GetSavedModel(record);
        if (!string.IsNullOrWhiteSpace(GetSavedSerial(record)) ||
            string.IsNullOrWhiteSpace(vendor) ||
            string.IsNullOrWhiteSpace(product) ||
            string.IsNullOrWhiteSpace(model) ||
            SavedControllers.Count(candidate => HaveSameModelAndHardware(record, candidate)) != 1)
        {
            return null;
        }

        ConnectedGamepad[] matches = ConnectedGamepads
            .Where(device =>
                !device.IsVirtualGamepad &&
                !matchedDeviceKeys.Contains(device.DeviceKey) &&
                string.Equals(device.Name, model, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    device.VendorId?.ToString("X4"),
                    vendor,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    device.ProductId?.ToString("X4"),
                    product,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (matches.Length > 1)
        {
            int bestScore = matches.Max(device =>
                device.PhysicalAxisCount + device.PhysicalButtonCount);
            ConnectedGamepad[] best = matches
                .Where(device =>
                    device.PhysicalAxisCount + device.PhysicalButtonCount == bestScore)
                .ToArray();
            return best.Length == 1 ? best[0] : null;
        }

        return null;
    }

    private static bool HaveSameModelAndHardware(ControllerRecord left, ControllerRecord right)
    {
        string? leftModel = GetSavedModel(left);
        string? rightModel = GetSavedModel(right);
        string? leftVendor = GetSavedEvidence(left, "VendorId");
        string? rightVendor = GetSavedEvidence(right, "VendorId");
        string? leftProduct = GetSavedEvidence(left, "ProductId");
        string? rightProduct = GetSavedEvidence(right, "ProductId");

        return !string.IsNullOrWhiteSpace(leftModel) &&
            !string.IsNullOrWhiteSpace(leftVendor) &&
            !string.IsNullOrWhiteSpace(leftProduct) &&
            string.Equals(leftModel, rightModel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(leftVendor, rightVendor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(leftProduct, rightProduct, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSavedModel(ControllerRecord record) =>
        string.IsNullOrWhiteSpace(record.Identity.Model)
            ? record.DisplayName
            : record.Identity.Model;

    private static bool HasMatchingHardwareIds(ControllerRecord record, ConnectedGamepad device)
    {
        string? vendor = GetSavedEvidence(record, "VendorId");
        string? product = GetSavedEvidence(record, "ProductId");
        return (vendor is null || device.VendorId?.ToString("X4") == vendor) &&
            (product is null || device.ProductId?.ToString("X4") == product);
    }

    private static IReadOnlyList<ControllerIdentityEvidence> CreateIdentityEvidence(
        ConnectedGamepad device)
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

        string? interfaceId = GetDeviceInterfaceId(device);
        if (!string.IsNullOrWhiteSpace(interfaceId))
        {
            evidence.Add(new ControllerIdentityEvidence
            {
                Source = "SDL2",
                Kind = "DeviceInterfaceId",
                Value = interfaceId
            });
        }

        return evidence;
    }

    private static string? GetDeviceInterfaceId(ConnectedGamepad device)
    {
        string? path = device.DevicePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string normalized = path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path[4..]
            : path;
        int collectionIndex = normalized.LastIndexOf("#{", StringComparison.Ordinal);
        if (collectionIndex >= 0)
        {
            normalized = normalized[..collectionIndex];
        }

        return normalized.Length == 0 ? null : normalized;
    }

    private static string? GetSavedSerial(ControllerRecord record) =>
        GetSavedEvidence(record, "SerialNumber");

    private static string? GetSavedEvidence(ControllerRecord record, string kind) =>
        record.Identity.Evidence
            .FirstOrDefault(evidence => evidence.Source == "SDL2" && evidence.Kind == kind)
            ?.Value;

    private void SetControllerLoadError(string title, Exception exception)
    {
        StatusTitle = title;
        StatusMessage = $"GDM no pudo cargar los mandos guardados; no se modificó ningún archivo. {exception.Message}";
        _appLogService?.Write(DiagnosticLogLevel.Error, "Mandos guardados", title, exception);
    }

    private void SetControllerSaveError(string title, Exception exception)
    {
        StatusTitle = title;
        StatusMessage = exception.Message;
        _appLogService?.Write(DiagnosticLogLevel.Error, "Mandos guardados", title, exception);
    }

    private void NotifyControllerCommandsCanExecuteChanged()
    {
        _refreshDevicesCommand.NotifyCanExecuteChanged();
        _saveControllerCommand.NotifyCanExecuteChanged();
        _removeSavedControllerCommand.NotifyCanExecuteChanged();
    }

    private void SetDetectionError(string title, Exception exception)
    {
        _pollTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _isMonitoring = false;
        StatusTitle = title;
        StatusMessage = $"No se pudo actualizar la lista de mandos. {exception.Message}";
        _appLogService?.Write(
            DiagnosticLogLevel.Error,
            "SDL2",
            title,
            exception);
    }

    private async Task HideRedundantInterfacesAsync(CancellationToken cancellationToken)
    {
        if (_gamepadDetectionService is null)
        {
            return;
        }

        foreach (ConnectedGamepad device in _gamepadDetectionService.RedundantInterfaces)
        {
            if (string.IsNullOrWhiteSpace(device.DevicePath) ||
                !_redundantHideAttempted.Add(device.DevicePath))
            {
                continue;
            }

            try
            {
                await _hidHideService.HideUntilGdmClosesAsync(device.DevicePath, cancellationToken);
                _appLogService?.Write(
                    DiagnosticLogLevel.Information,
                    "HidHide",
                    $"Interfaz redundante de «{device.Name}» ocultada para otras aplicaciones.");
            }
            catch (OperationCanceledException)
            {
                _redundantHideAttempted.Remove(device.DevicePath);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
            {
                _appLogService?.Write(
                    DiagnosticLogLevel.Warning,
                    "HidHide",
                    $"No se pudo ocultar la interfaz redundante de «{device.Name}»: {exception.Message}");
            }
        }
    }

    private static string FormatDeviceChange(IReadOnlyList<ConnectedGamepad> devices)
    {
        if (devices.Count == 0)
        {
            return "SDL2 no encontró mandos conectados y accesibles.";
        }

        string descriptions = string.Join(
            "; ",
            devices.Select(device =>
                $"{device.Name} ({device.HardwareIdLabel}, {device.DeviceKey}, " +
                $"Serial={device.SerialNumber ?? "(n/d)"}, ejes={device.PhysicalAxisCount}, " +
                $"botones={device.PhysicalButtonCount}, mapeo={(device.HasStandardMapping ? "SI" : "NO")}, " +
                $"Ruta={device.DevicePath ?? "(n/d)"}, GUID={device.SdlGuid ?? "(n/d)"})"));
        return $"SDL2 detectó {devices.Count} mando(s): {descriptions}.";
    }

    public void Dispose()
    {
        StopMonitoring();
        _testerInputTimer.Stop();
        _testerInputTimer.Tick -= OnTesterInputTimerTick;
        _idlePowerOffTimer.Stop();
        _bluetoothStatusTimer.Stop();
        _idlePowerOffTimer.Tick -= OnIdlePowerOffTimerTick;
        _testerViewModel.PropertyChanged -= OnTesterPropertyChanged;
        Profiles.LedColorChanged -= OnLedColorChanged;
        Tester.Dispose();
        Bluetooth.Dispose();
        _pollTimer?.Dispose();
        _deviceEventTimer?.Dispose();
        if (_gamepadDetectionService is not null)
        {
            _gamepadDetectionService.DeviceMembershipChanged -= OnSdlDeviceMembershipChanged;
        }
        if (_appLogService is not null)
        {
            _appLogService.EntryAdded -= OnLogEntryAdded;
        }

        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }

    public Task RestoreHidHideBeforeCloseAsync() =>
        HidHide.RestoreManagedChangesAsync(
            Profiles.GetAutoHiddenDeviceExitPolicies(),
            keepUnlistedDevicesHidden: true);

    private void OnLogEntryAdded(DiagnosticLogEntry entry)
    {
        if (_dispatcher.CheckAccess())
        {
            AddLogEntry(entry);
            return;
        }

        _dispatcher.BeginInvoke(() => AddLogEntry(entry));
    }

    private void AddLogEntry(DiagnosticLogEntry entry)
    {
        LogEntries.Insert(0, entry);
        if (LogEntries.Count > 1000)
        {
            LogEntries.RemoveAt(LogEntries.Count - 1);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Assembly ApplicationAssembly =>
        typeof(MainWindowViewModel).Assembly;

    private void RefreshBluetoothStatus()
    {
        BluetoothRadioState state = BluetoothRadioService.GetState();
        BluetoothStatusText = state switch
        {
            BluetoothRadioState.On => "Bluetooth: activado",
            BluetoothRadioState.Off => "Bluetooth: desactivado",
            _ => "Bluetooth: no detectado"
        };
        BluetoothStatusForeground = state == BluetoothRadioState.On
            ? BluetoothOnBrush
            : BluetoothOffBrush;

        if (state == BluetoothRadioState.Off && !_bluetoothWarningShown)
        {
            _bluetoothWarningShown = true;
            _toastService.ShowEvent(
                Models.NotificationEvents.BluetoothOff,
                "Bluetooth desactivado",
                "Activa el Bluetooth de Windows para conectar mandos inalámbricos por Bluetooth.");
        }
    }

    /// <summary>
    /// Compara la lista de mandos físicos con la anterior y emite toasts de
    /// conexión/desconexión. La primera llamada solo establece la línea base
    /// para no notificar todos los mandos ya conectados al abrir GDM. Los
    /// mandos virtuales y las interfaces redundantes se ignoran.
    /// </summary>
    private void NotifyControllerPresenceChanges(IReadOnlyList<ConnectedGamepad> displayDevices)
    {
        long now = Environment.TickCount64;
        var current = new Dictionary<string, ConnectedGamepad>(StringComparer.Ordinal);
        foreach (ConnectedGamepad device in displayDevices)
        {
            if (device.IsVirtualGamepad || device.IsRedundantInterface)
            {
                continue;
            }

            current[PhysicalNotifyIdentity(device)] = device;
        }

        if (!_deviceNotifyBaselineDone)
        {
            _deviceNotifyBaselineDone = true;
            foreach (KeyValuePair<string, ConnectedGamepad> pair in current)
            {
                _deviceNotifyStates[pair.Key] = new DeviceNotifyTrack(pair.Value);
            }

            return;
        }

        // Adopción de identidad: si un mando cambia de clave de notificación
        // (p. ej. la ruta SDL pasa de «XInput#N» a la ruta HID real, o el
        // número de serie aparece en una enumeración posterior), no debe
        // contar como desconexión + conexión.
        var adoptedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, DeviceNotifyTrack> pair in _deviceNotifyStates.ToArray())
        {
            if (current.ContainsKey(pair.Key))
            {
                continue;
            }

            ConnectedGamepad? match = current.Values.FirstOrDefault(
                candidate => !adoptedKeys.Contains(PhysicalNotifyIdentity(candidate)) &&
                    IsSamePhysicalNotifyDevice(pair.Value.Device, candidate));
            if (match is null)
            {
                continue;
            }

            string newKey = PhysicalNotifyIdentity(match);
            if (_deviceNotifyStates.ContainsKey(newKey))
            {
                continue;
            }

            _deviceNotifyStates.Remove(pair.Key);
            pair.Value.Device = match;
            pair.Value.MissingSinceMs = null;
            _deviceNotifyStates[newKey] = pair.Value;
            adoptedKeys.Add(newKey);
        }

        foreach (KeyValuePair<string, DeviceNotifyTrack> pair in _deviceNotifyStates.ToArray())
        {
            if (current.ContainsKey(pair.Key))
            {
                continue;
            }

            DeviceNotifyTrack track = pair.Value;
            if (track.MissingSinceMs is not long missingSince)
            {
                track.MissingSinceMs = now;
                continue;
            }

            if (now - missingSince < DeviceDisconnectNotifyGraceMs)
            {
                continue;
            }

            _deviceNotifyStates.Remove(pair.Key);
            // Al desconectarse no tiene sentido mostrar transporte ni batería
            // (datos de la última lectura, ya obsoletos): solo el nombre.
            _toastService.ShowEvent(
                Models.NotificationEvents.ControllerDisconnected,
                "Mando desconectado",
                track.Device.Name);
        }

        foreach (KeyValuePair<string, ConnectedGamepad> pair in current)
        {
            if (adoptedKeys.Contains(pair.Key))
            {
                continue;
            }

            ConnectedGamepad device = pair.Value;
            if (_deviceNotifyStates.TryGetValue(pair.Key, out DeviceNotifyTrack? track))
            {
                track.Device = device;
                track.MissingSinceMs = null;
                continue;
            }

            _deviceNotifyStates[pair.Key] = new DeviceNotifyTrack(device);
            ShowControllerConnectedNotification(device);
        }
    }

    /// <summary>
    /// Compara dos enumeraciones del mismo mando físico aunque SDL las
    /// identifique con claves distintas (serie tardía, ruta XInput→HID, etc.).
    /// </summary>
    private static bool IsSamePhysicalNotifyDevice(ConnectedGamepad a, ConnectedGamepad b)
    {
        if (!string.IsNullOrWhiteSpace(a.SerialNumber) &&
            !string.IsNullOrWhiteSpace(b.SerialNumber))
        {
            return string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(a.DevicePath) &&
            !string.IsNullOrWhiteSpace(b.DevicePath))
        {
            return string.Equals(a.DevicePath, b.DevicePath, StringComparison.OrdinalIgnoreCase);
        }

        return a.VendorId is not null && a.VendorId == b.VendorId &&
            a.ProductId is not null && a.ProductId == b.ProductId &&
            string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(a.SerialNumber) &&
            string.IsNullOrWhiteSpace(b.SerialNumber);
    }

    private static string PhysicalNotifyIdentity(ConnectedGamepad device) =>
        !string.IsNullOrWhiteSpace(device.SerialNumber)
            ? $"serial:{device.SerialNumber}"
            : !string.IsNullOrWhiteSpace(device.DevicePath)
                ? $"path:{device.DevicePath}"
                : $"vidpid:{device.VendorId?.ToString("X4")}:{device.ProductId?.ToString("X4")}:{device.Name}";

    private void ShowControllerConnectedNotification(ConnectedGamepad device)
    {
        ControllerListItem? item = ControllerItems.FirstOrDefault(
            entry => entry.ConnectedDevice?.DeviceKey == device.DeviceKey);
        ControllerRecord? record = item?.SavedRecord;

        if (record is null)
        {
            ShowNewControllerNotification(device);
            return;
        }

        ShowSavedControllerConnectedNotification(device, record);
    }

    /// <summary>
    /// Mando físico sin guardar: título «Nueva conexión» con nombre,
    /// fabricante, batería y el combo configurado para guardarlo.
    /// </summary>
    private void ShowNewControllerNotification(ConnectedGamepad device)
    {
        var lines = new List<string> { $"Mando: {device.Name}" };

        string manufacturer = ResolveManufacturerLabel(device);
        lines.Add($"Fabricante: {manufacturer}");
        if (device.HasBatteryInfo)
        {
            lines.Add(device.BatteryInfoText);
        }

        NewControllerSaveCombo saveCombo = Profiles.GlobalPreset.SaveNewControllerCombo;
        if (saveCombo.Enabled && saveCombo.Buttons.Count > 0)
        {
            lines.Add(
                $"Para guardar: mantén {FormatComboButtons(saveCombo.Buttons, saveCombo.HoldMilliseconds)}");
        }

        _toastService.ShowEvent(
            Models.NotificationEvents.ControllerConnected,
            "Nueva conexión", [.. lines]);
    }

    /// <summary>
    /// Mando físico guardado: título con el nombre del mando y líneas con
    /// conexión, batería, perfil y tipo de mando virtual.
    /// </summary>
    private void ShowSavedControllerConnectedNotification(
        ConnectedGamepad device,
        ControllerRecord record)
    {
        string profileName = record.ProfileId is Guid profileId
            ? Profiles.Profiles.FirstOrDefault(profile => profile.ProfileId == profileId)
                  ?.DisplayName ?? "Perfil por mando"
            : ControllerProfile.GlobalPresetName;
        string virtualType = Profiles.ResolveVirtualGamepadType(record);

        var lines = new List<string>
        {
            $"Conexión: {device.ConnectionTransportText ?? "Desconocida"}"
        };
        if (device.HasBatteryInfo)
        {
            lines.Add(device.BatteryInfoText);
        }

        lines.Add($"Perfil: {profileName}");
        lines.Add($"Mando virtual: {VirtualGamepadTypes.DisplayName(virtualType)}");

        _toastService.ShowEvent(
            Models.NotificationEvents.ControllerConnected,
            device.Name, [.. lines]);
    }

    private static string FormatComboButtons(IReadOnlyList<string> buttonIds, int holdMilliseconds)
    {
        string names = string.Join(
            " + ",
            buttonIds.Select(id => GamepadControlCatalog.Buttons
                .FirstOrDefault(option => option.Id == id)?.DisplayName ?? id));
        double seconds = holdMilliseconds / 1000d;
        return seconds > 0 ? $"{names} durante {seconds:0.#} s" : names;
    }

    /// <summary>
    /// Nombre del fabricante según el VID USB; si se desconoce se muestran
    /// los identificadores de hardware.
    /// </summary>
    private static string ResolveManufacturerLabel(ConnectedGamepad device) =>
        device.ManufacturerLabel;

    private sealed class DeviceNotifyTrack(ConnectedGamepad device)
    {
        public ConnectedGamepad Device { get; set; } = device;
        public long? MissingSinceMs { get; set; }
    }
    private static string GetBuildTimestampLabel()
    {
        string assemblyPath = ApplicationAssembly.Location;
        if (!File.Exists(assemblyPath))
        {
            return "Compilado: no disponible";
        }

        DateTime compiledAt = File.GetLastWriteTimeUtc(assemblyPath).ToLocalTime();
        return $"Compilado: {compiledAt:dd/MM/yyyy HH:mm:ss}";
    }

    private sealed class NavigationCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncParameterCommand(
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
