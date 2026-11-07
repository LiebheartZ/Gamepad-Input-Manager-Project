using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly Dispatcher _dispatcher;
    private readonly AsyncCommand _refreshDevicesCommand;
    private readonly Timer? _pollTimer;
    private CancellationTokenSource? _refreshCancellation;
    private ConnectedGamepad[]? _lastLoggedDevices;
    private int _isPollQueued;
    private bool _isRefreshing;
    private bool _isMonitoring;
    private string _currentSection = "Inicio";
    private string _sectionTitle = "Inicio";
    private string _sectionDescription = "Base de la nueva versión de Gamepad Device Manager.";
    private string _statusTitle = "La arquitectura inicial está lista";
    private string _statusMessage =
        "La aplicación está organizada por categorías. La detección y configuración de mandos se implementarán en fases posteriores.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand NavigateCommand { get; }
    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;
    public ObservableCollection<ConnectedGamepad> ConnectedGamepads { get; } = [];
    public ObservableCollection<DiagnosticLogEntry> LogEntries { get; } = [];
    public string? LogFilePath => _appLogService?.LogFilePath;
    public string BuildLabel { get; } =
        $"BUILD {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "local"}";

    public bool IsControllerSection => CurrentSection == "Mandos";
    public bool IsLogSection => CurrentSection == "Registro";
    public bool ShowStatusCard => !IsLogSection;
    public bool HasConnectedGamepads => ConnectedGamepads.Count > 0;
    public bool ShowEmptyDevicesMessage => IsControllerSection && !HasConnectedGamepads;
    public bool ShowConnectedGamepads => IsControllerSection && HasConnectedGamepads;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                _refreshDevicesCommand.NotifyCanExecuteChanged();
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
        IAppLogService? appLogService = null)
    {
        _gamepadDetectionService = gamepadDetectionService;
        _appLogService = appLogService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        NavigateCommand = new NavigationCommand(Navigate);
        _refreshDevicesCommand = new AsyncCommand(
            () => RefreshDevicesAsync(forceRescan: true),
            () => !IsRefreshing);
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

        if (_appLogService is not null)
        {
            foreach (DiagnosticLogEntry entry in _appLogService.GetRecentEntries())
            {
                LogEntries.Add(entry);
            }

            _appLogService.EntryAdded += OnLogEntryAdded;
        }
    }

    public async Task RefreshDevicesAsync(bool forceRescan = false)
    {
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
                    devices.Count == 0
                        ? "SDL2 no encontró mandos conectados y accesibles."
                        : $"SDL2 detectó {devices.Count} mando(s) conectado(s).");
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
            _ = RefreshDevicesAsync(forceRescan: true);
        }
        else
        {
            StopMonitoring();
        }
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
}
