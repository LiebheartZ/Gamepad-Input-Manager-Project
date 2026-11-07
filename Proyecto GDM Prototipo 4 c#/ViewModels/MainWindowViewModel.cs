using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IGamepadDetectionService? _gamepadDetectionService;
    private readonly IAppLogService? _appLogService;
    private readonly IControllerRepository? _controllerRepository;
    private readonly Func<ControllerRecord, bool>? _confirmRemoveSavedController;
    private readonly Dispatcher _dispatcher;
    private readonly AsyncCommand _refreshDevicesCommand;
    private readonly AsyncParameterCommand _saveControllerCommand;
    private readonly AsyncParameterCommand _removeSavedControllerCommand;
    private readonly Timer? _pollTimer;
    private readonly Dictionary<Guid, string> _sessionSavedDeviceKeys = [];
    private Task? _controllerInitializationTask;
    private CancellationTokenSource? _refreshCancellation;
    private ConnectedGamepad[]? _lastLoggedDevices;
    private int _isPollQueued;
    private bool _isRefreshing;
    private bool _isMonitoring;
    private bool _isControllerDataLoaded;
    private bool _isSavingController;
    private bool _isRemovingController;
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
    public string? LogFilePath => _appLogService?.LogFilePath;
    public string BuildLabel { get; } =
        $"BUILD {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "local"}";

    public bool IsControllerSection => CurrentSection == "Mandos";
    public bool IsLogSection => CurrentSection == "Registro";
    public bool ShowStatusCard => !IsLogSection;
    public bool HasConnectedGamepads => ConnectedGamepads.Count > 0;
    public bool ShowEmptyDevicesMessage =>
        IsControllerSection && _isControllerDataLoaded && ControllerItems.Count == 0;
    public bool ShowConnectedGamepads => IsControllerSection && ControllerItems.Count > 0;
    public bool IsControllerDataLoaded => _isControllerDataLoaded;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                _refreshDevicesCommand.NotifyCanExecuteChanged();
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
                OnPropertyChanged(nameof(IsLogSection));
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
        Func<ControllerRecord, bool>? confirmRemoveSavedController = null)
    {
        _gamepadDetectionService = gamepadDetectionService;
        _appLogService = appLogService;
        _controllerRepository = controllerRepository;
        _confirmRemoveSavedController = confirmRemoveSavedController;
        _dispatcher = Dispatcher.CurrentDispatcher;
        NavigateCommand = new NavigationCommand(Navigate);
        _refreshDevicesCommand = new AsyncCommand(
            () => RefreshDevicesAsync(forceRescan: true),
            () => !IsRefreshing &&
                (_controllerRepository is null || _isControllerDataLoaded));
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
            _pollTimer = new Timer(
                OnPollTimerElapsed,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        ConnectedGamepads.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConnectedGamepads));
            OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
            OnPropertyChanged(nameof(ShowConnectedGamepads));
        };
        SavedControllers.CollectionChanged += (_, _) => RebuildControllerItems();

        if (_appLogService is not null)
        {
            foreach (DiagnosticLogEntry entry in _appLogService.GetRecentEntries())
            {
                LogEntries.Add(entry);
            }

            _appLogService.EntryAdded += OnLogEntryAdded;
        }
    }

    public Task InitializeAsync()
    {
        if (_isControllerDataLoaded)
        {
            return Task.CompletedTask;
        }

        return _controllerInitializationTask ??= LoadSavedControllersAsync();
    }

    private async Task LoadSavedControllersAsync()
    {
        if (_controllerRepository is null)
        {
            _isControllerDataLoaded = true;
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

            if (!ConnectedGamepads.SequenceEqual(devices))
            {
                ConnectedGamepads.Clear();
                foreach (ConnectedGamepad device in devices)
                {
                    ConnectedGamepads.Add(device);
                }
            }
            RebuildControllerItems();

            StatusTitle = devices.Count == 0
                ? "SDL2 no ve mandos accesibles"
                : $"{devices.Count} mando{(devices.Count == 1 ? string.Empty : "s")} conectado{(devices.Count == 1 ? string.Empty : "s")}";
            StatusMessage = devices.Count == 0
                ? "SDL2 no detecta mandos accesibles. Si usas DS4Windows o HidHide, el mando físico puede estar oculto para GDM. Añade GDM a la lista de aplicaciones permitidas de HidHide; no hace falta desactivar la protección global."
                : "La lista muestra únicamente dispositivos que SDL2 detecta conectados y accesibles en este momento.";

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

    public void StartMonitoring()
    {
        if (_gamepadDetectionService is null || _isMonitoring)
        {
            return;
        }

        _isMonitoring = true;
        _pollTimer?.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void StopMonitoring()
    {
        _pollTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _isMonitoring = false;
        _refreshCancellation?.Cancel();
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
                if (_isMonitoring)
                {
                    await RefreshDevicesAsync();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isPollQueued, 0);
            }
        }));
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
                "Módulo pendiente de integración",
                "La configuración persistente se añadirá junto con los servicios JSON del núcleo."
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

        if (CurrentSection == "Mandos")
        {
            StartMonitoring();
            _ = InitializeAndRefreshAsync();
        }
        else
        {
            StopMonitoring();
        }
    }

    private async Task InitializeAndRefreshAsync()
    {
        await InitializeAsync();
        if (_controllerRepository is not null && !_isControllerDataLoaded)
        {
            return;
        }

        await RefreshDevicesAsync(forceRescan: true);
    }

    public async Task SaveControllerAsync(ControllerListItem item)
    {
        if (item.ConnectedDevice is not { } device ||
            _controllerRepository is null ||
            !_isControllerDataLoaded ||
            _gamepadDetectionService is null ||
            _isSavingController ||
            _isRemovingController ||
            IsRefreshing)
        {
            return;
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
                return;
            }

            if (HasSavedSerial(currentDevice))
            {
                StatusTitle = "El mando ya está guardado";
                StatusMessage = "GDM ya tiene un registro con el identificador individual de este mando.";
                return;
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
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _controllerRepository.SaveAsync(record);
            SavedControllers.Add(record);
            _sessionSavedDeviceKeys[record.ControllerId] = currentDevice.DeviceKey;
            RebuildControllerItems();
            StatusTitle = "Mando guardado";
            StatusMessage = string.IsNullOrWhiteSpace(currentDevice.SerialNumber)
                ? "El registro se guardó, pero SDL2 no proporcionó un identificador individual estable. GDM lo conservará como desconectado y no lo asociará automáticamente al reconectarlo."
                : "El registro del mando se guardó. GDM lo asociará al reconectarlo solo si el identificador individual coincide de forma única.";
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Mandos",
                $"Se guardó el mando «{record.DisplayName}».");
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
        finally
        {
            _isSavingController = false;
            NotifyControllerCommandsCanExecuteChanged();
        }
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
        foreach (ControllerRecord record in SavedControllers)
        {
            ConnectedGamepad? matchingDevice = FindUniqueSerialMatch(record)
                ?? FindSessionDeviceMatch(record);
            if (matchingDevice is not null)
            {
                matchedDeviceKeys.Add(matchingDevice.DeviceKey);
            }

            ControllerItems.Add(new ControllerListItem(matchingDevice, record));
        }

        foreach (ConnectedGamepad device in ConnectedGamepads)
        {
            if (!matchedDeviceKeys.Contains(device.DeviceKey))
            {
                ControllerItems.Add(new ControllerListItem(device, null));
            }
        }

        OnPropertyChanged(nameof(ShowEmptyDevicesMessage));
        OnPropertyChanged(nameof(ShowConnectedGamepads));
        _saveControllerCommand.NotifyCanExecuteChanged();
        _removeSavedControllerCommand.NotifyCanExecuteChanged();
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
                string.Equals(device.SerialNumber, serial, StringComparison.Ordinal) &&
                HasMatchingHardwareIds(record, device))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private ConnectedGamepad? FindSessionDeviceMatch(ControllerRecord record)
    {
        if (!_sessionSavedDeviceKeys.TryGetValue(record.ControllerId, out string? deviceKey))
        {
            return null;
        }

        ConnectedGamepad? match = ConnectedGamepads.SingleOrDefault(
            device => device.DeviceKey == deviceKey);
        if (match is null)
        {
            _sessionSavedDeviceKeys.Remove(record.ControllerId);
            return null;
        }

        return HasMatchingHardwareIds(record, match) ? match : null;
    }

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

        return evidence;
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

    private static string FormatDeviceChange(IReadOnlyList<ConnectedGamepad> devices)
    {
        if (devices.Count == 0)
        {
            return "SDL2 no encontró mandos conectados y accesibles.";
        }

        string descriptions = string.Join(
            "; ",
            devices.Select(device =>
                $"{device.Name} ({device.HardwareIdLabel}, {device.DeviceKey})"));
        return $"SDL2 detectó {devices.Count} mando(s): {descriptions}.";
    }

    public void Dispose()
    {
        StopMonitoring();
        _pollTimer?.Dispose();
        if (_appLogService is not null)
        {
            _appLogService.EntryAdded -= OnLogEntryAdded;
        }

        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }

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
