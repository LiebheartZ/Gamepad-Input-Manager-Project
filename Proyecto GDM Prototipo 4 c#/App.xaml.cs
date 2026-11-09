using GDM.Models;
using GDM.Services;
using GDM.Services.Abstractions;
using GDM.Services.Persistence;
using GDM.Views;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace GDM;

public partial class App : System.Windows.Application
{
    public IStoragePathService StoragePaths { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = new();
    public IAppLogService? LogService { get; private set; }
    private IAppSettingsRepository? _settingsRepository;
    private bool _isHandlingWindowClose;
    private bool _allowWindowClose;
    private bool _settingsLoadedSuccessfully;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private bool _activationPending;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceCoordinator = new SingleInstanceCoordinator();
        bool isStartupVirtualGamepad = e.Args.Contains(
            "--start-virtual-gamepad",
            StringComparer.OrdinalIgnoreCase);
        if (!_singleInstanceCoordinator.TryBecomePrimary())
        {
            if (isStartupVirtualGamepad)
            {
                try
                {
                    _singleInstanceCoordinator.WaitForPrimaryHandoff();
                }
                catch (TimeoutException exception)
                {
                    MessageBox.Show(
                        exception.Message,
                        "GDM — Inicio elevado",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }
            else
            {
                MessageBox.Show(
                    "GDM ya se está ejecutando. Se abrirá la ventana de la instancia existente.",
                    "GDM ya está abierto",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                bool activated = await SingleInstanceCoordinator.NotifyPrimaryAsync();
                if (!activated)
                {
                    MessageBox.Show(
                        "Ya hay una instancia de GDM en ejecución, pero no se pudo activar su ventana. " +
                        "Comprueba que la instancia existente no esté bloqueada.",
                        "GDM ya está abierto",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                Shutdown();
                return;
            }
        }

        StoragePaths = new LocalStoragePathService();
        var logService = new AppLogService(StoragePaths);
        LogService = logService;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        logService.Write(DiagnosticLogLevel.Information, "GDM", "Inicio de la aplicación.");
        _ = _singleInstanceCoordinator.StartListeningAsync(
            ActivatePrimaryWindow,
            exception => logService.Write(
                DiagnosticLogLevel.Warning,
                "Instancia única",
                "No se pudo procesar una solicitud de activación de GDM.",
                exception));
        var settingsRepository = new JsonAppSettingsRepository(StoragePaths);
        _settingsRepository = settingsRepository;

        try
        {
            Settings = await _settingsRepository.LoadAsync();
            _settingsLoadedSuccessfully = true;
        }
        catch (InvalidDataException exception)
        {
            logService.Write(DiagnosticLogLevel.Error, "Configuración", "Los ajustes están dañados o tienen una versión no compatible.", exception);
            ShowSettingsWarning(exception);
        }
        catch (IOException exception)
        {
            logService.Write(DiagnosticLogLevel.Error, "Configuración", "No se pudieron leer los ajustes.", exception);
            ShowSettingsWarning(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            logService.Write(DiagnosticLogLevel.Error, "Configuración", "No hay permisos para leer los ajustes.", exception);
            ShowSettingsWarning(exception);
        }

        ApplyTheme(Settings.Theme);
        var windowsStartupService = new WindowsStartupService();
        if (Settings.StartWithWindows && !Settings.StartWithWindowsAsTask)
        {
            try
            {
                await windowsStartupService.ConfigureAsync(
                    true,
                    Settings.StartMinimized,
                    startWithWindowsAsTask: false);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or
                System.Security.SecurityException or
                InvalidOperationException or
                Win32Exception or
                IOException)
            {
                logService.Write(
                    DiagnosticLogLevel.Error,
                    "Configuración",
                    "No se pudo actualizar la ruta de GDM en el inicio de Windows.",
                    exception);
            }
        }

        var settingsViewModel = new GDM.ViewModels.SettingsViewModel(
            Settings,
            settingsRepository,
            updatedSettings =>
            {
                Settings = updatedSettings;
                ApplyTheme(updatedSettings.Theme);
            },
            logService,
            windowsStartupService);
        var gamepadDetectionService = new Sdl2GamepadDetectionService();
        var controllerRepository = new JsonControllerRepository(StoragePaths);
        var profileRepository = new JsonProfileRepository(StoragePaths);
        ConnectedGamepad? startupVirtualGamepad = ParseStartupVirtualGamepad(
            e.Args,
            logService);
        var hidHideService = new HidHideService(
            Path.Combine(StoragePaths.RootDirectory, "HidHide", "recovery.json"),
            (level, message, exception) => logService.Write(
                level,
                "HidHide diagnóstico",
                message,
                exception));
        try
        {
            await hidHideService.RecoverPendingChangesAsync();
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            InvalidOperationException or
            Win32Exception)
        {
            logService.Write(
                DiagnosticLogLevel.Error,
                "HidHide",
                "No se pudo recuperar una operación pendiente de ocultamiento.",
                exception);
            MessageBox.Show(
                "GDM no pudo recuperar automáticamente un cambio pendiente de HidHide. " +
                "La operación se volverá a intentar antes de ocultar o restaurar mandos.\n\n" +
                exception.Message,
                "GDM — Recuperación de HidHide pendiente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        try
        {
            if (await hidHideService.EnsureCurrentApplicationIsWhitelistedAsync())
            {
                logService.Write(
                    DiagnosticLogLevel.Information,
                    "HidHide",
                    "GDM está autorizado antes de inicializar SDL2.");
            }
            else
            {
                logService.Write(
                    DiagnosticLogLevel.Information,
                    "HidHide",
                    "HidHide no está disponible; no se pudo preparar la autorización de GDM.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            InvalidOperationException or
            Win32Exception)
        {
            logService.Write(
                DiagnosticLogLevel.Error,
                "HidHide",
                "No se pudo autorizar GDM antes de inicializar SDL2.",
                exception);
            MessageBox.Show(
                "GDM no pudo preparar su acceso al mando físico antes de iniciar SDL2. " +
                "El mando podría dejar de enviar entradas si se oculta automáticamente.\n\n" +
                exception.Message,
                "GDM — Acceso de HidHide",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var virtualGamepadService = new HidMaestroVirtualGamepadService(gamepadDetectionService);
        var window = new MainWindow(
            Settings,
            gamepadDetectionService,
            logService,
            controllerRepository,
            settingsViewModel,
            profileRepository,
            virtualGamepadService,
            startupVirtualGamepad,
            hidHideService,
            e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase));
        window.Closing += HandleWindowClosing;
        MainWindow = window;
        window.Show();
        if (_activationPending)
        {
            window.ActivateFromExternalRequest();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;
        base.OnExit(e);
    }

    private void ActivatePrimaryWindow()
    {
        if (MainWindow is MainWindow window)
        {
            window.ActivateFromExternalRequest();
        }
        else
        {
            _activationPending = true;
        }
    }

    private static ConnectedGamepad? ParseStartupVirtualGamepad(
        string[] args,
        IAppLogService logService)
    {
        int optionIndex = Array.IndexOf(args, "--start-virtual-gamepad");
        if (optionIndex < 0)
        {
            return null;
        }

        if (optionIndex + 1 >= args.Length)
        {
            var exception = new InvalidDataException(
                "Falta la identidad del mando para iniciar la salida virtual.");
            logService.Write(
                DiagnosticLogLevel.Error,
                "Mando virtual",
                "No se pudo interpretar la solicitud de inicio recibida.",
                exception);
            MessageBox.Show(
                exception.Message,
                "GDM — Inicio de emulación",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return null;
        }

        try
        {
            byte[] serializedDevice = Convert.FromBase64String(args[optionIndex + 1]);
            ConnectedGamepad? device = JsonSerializer.Deserialize<ConnectedGamepad>(serializedDevice);
            return device
                ?? throw new InvalidDataException("La identidad recibida para el mando está vacía.");
        }
        catch (FormatException exception)
        {
            LogInvalidStartupRequest(logService, exception);
        }
        catch (JsonException exception)
        {
            LogInvalidStartupRequest(logService, exception);
        }
        catch (InvalidDataException exception)
        {
            LogInvalidStartupRequest(logService, exception);
        }

        return null;
    }

    private static void LogInvalidStartupRequest(
        IAppLogService logService,
        Exception exception)
    {
        logService.Write(
            DiagnosticLogLevel.Error,
            "Mando virtual",
            "No se pudo interpretar la solicitud de inicio recibida.",
            exception);
        MessageBox.Show(
            $"No se pudo iniciar automáticamente el mando virtual.\n\n{exception.Message}",
            "GDM — Inicio de emulación",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void ApplyTheme(string theme)
    {
        bool isDark = theme switch
        {
            "System" => IsSystemDarkTheme(),
            "Light" => false,
            "Dark" => true,
            _ => throw new InvalidDataException($"Tema de GDM no compatible: {theme}.")
        };

        SetBrushColor(
            "WindowBrush",
            isDark ? Color.FromRgb(0x17, 0x1C, 0x22) : Color.FromRgb(0xF4, 0xF7, 0xFA));
        SetBrushColor(
            "PanelBrush",
            isDark ? Color.FromRgb(0x22, 0x2A, 0x33) : Color.FromRgb(0xFF, 0xFF, 0xFF));
        SetBrushColor(
            "PanelRaisedBrush",
            isDark ? Color.FromRgb(0x30, 0x3B, 0x47) : Color.FromRgb(0xE6, 0xEE, 0xF5));
        SetBrushColor(
            "AccentBrush",
            isDark ? Color.FromRgb(0x55, 0xA8, 0xFF) : Color.FromRgb(0x14, 0x77, 0xD4));
        SetBrushColor(
            "PrimaryTextBrush",
            isDark ? Color.FromRgb(0xE7, 0xED, 0xF3) : Color.FromRgb(0x1C, 0x2A, 0x36));
        SetBrushColor(
            "SecondaryTextBrush",
            isDark ? Color.FromRgb(0xAA, 0xB7, 0xC4) : Color.FromRgb(0x62, 0x73, 0x83));
        SetBrushColor(
            "BorderBrush",
            isDark ? Color.FromRgb(0x3B, 0x47, 0x54) : Color.FromRgb(0xD9, 0xE2, 0xEA));
        SetBrushColor(
            "ButtonBrush",
            isDark ? Color.FromRgb(0x30, 0x3B, 0x47) : Color.FromRgb(0xE6, 0xEE, 0xF5));
        SetBrushColor(
            "ButtonHoverBrush",
            isDark ? Color.FromRgb(0x37, 0x8B, 0xDB) : Color.FromRgb(0x14, 0x77, 0xD4));
        SetBrushColor(
            "ButtonPressedBrush",
            isDark ? Color.FromRgb(0x25, 0x67, 0xAA) : Color.FromRgb(0x10, 0x5E, 0xA8));
        SetBrushColor(
            "ButtonBorderBrush",
            isDark ? Color.FromRgb(0x50, 0x60, 0x70) : Color.FromRgb(0xC3, 0xD0, 0xDC));
        SetBrushColor(
            "SelectionBrush",
            isDark ? Color.FromRgb(0x37, 0x8B, 0xDB) : Color.FromRgb(0x14, 0x77, 0xD4));
        SetBrushColor(
            "ConnectedStatusBrush",
            isDark ? Color.FromRgb(0x65, 0xD6, 0x9C) : Color.FromRgb(0x17, 0x7A, 0x56));
    }

    private bool IsSystemDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int lightTheme && lightTheme == 0;
        }
        catch (IOException exception)
        {
            LogService?.Write(
                DiagnosticLogLevel.Warning,
                "Configuración",
                "No se pudo consultar el tema de Windows; se usará el tema claro.",
                exception);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            LogService?.Write(
                DiagnosticLogLevel.Warning,
                "Configuración",
                "No hay permisos para consultar el tema de Windows; se usará el tema claro.",
                exception);
            return false;
        }
    }

    private void SetBrushColor(string resourceKey, System.Windows.Media.Color color)
    {
        if (!Resources.Contains(resourceKey))
        {
            throw new InvalidOperationException(
                $"No se encontró el recurso de color requerido «{resourceKey}».");
        }

        Resources[resourceKey] = new SolidColorBrush(color);
    }

    private async void HandleWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose)
        {
            return;
        }

        if (sender is not MainWindow window)
        {
            return;
        }

        if (!window.IsExitRequested)
        {
            e.Cancel = true;
            window.HideToTray();
            return;
        }

        e.Cancel = true;
        if (_isHandlingWindowClose)
        {
            return;
        }

        _isHandlingWindowClose = true;

        try
        {
            if (!window.IsElevatedRelaunch)
            {
                try
                {
                    await window.RestoreHidHideBeforeCloseAsync();
                }
                catch (Exception exception) when (
                    exception is IOException or
                    InvalidDataException or
                    UnauthorizedAccessException or
                    InvalidOperationException or
                    Win32Exception)
                {
                    LogService?.Write(
                        DiagnosticLogLevel.Error,
                        "HidHide",
                        "No se pudo restaurar la lista negra al cerrar GDM.",
                        exception);
                    MessageBoxResult result = MessageBox.Show(
                        window,
                        "No se pudo restaurar la lista negra de HidHide. Si cierras GDM, los mandos afectados pueden permanecer ocultos " +
                        "hasta que GDM vuelva a iniciarse; el diario se conservará para reintentar la restauración.\n\n" +
                        $"{exception.Message}\n\n¿Deseas cerrar GDM de todos modos?",
                        "GDM — No se pudo restaurar HidHide",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
            }

            if (_settingsLoadedSuccessfully && _settingsRepository is not null)
            {
                await SaveWindowSizeAsync(window);
            }

            _allowWindowClose = true;
            _ = window.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(window.Close));
        }
        finally
        {
            _isHandlingWindowClose = false;
        }
    }

    private async Task SaveWindowSizeAsync(Window window)
    {
        Rect bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;

        Settings = Settings with
        {
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height
        };

        try
        {
            await _settingsRepository!.SaveAsync(Settings);
            LogService?.Write(DiagnosticLogLevel.Information, "Configuración", "Se guardó el tamaño de la ventana.");
        }
        catch (InvalidDataException exception)
        {
            LogService?.Write(DiagnosticLogLevel.Error, "Configuración", "Los ajustes no superaron la validación al guardarse.", exception);
            ShowSettingsWarning(exception);
        }
        catch (IOException exception)
        {
            LogService?.Write(DiagnosticLogLevel.Error, "Configuración", "No se pudieron guardar los ajustes.", exception);
            ShowSettingsWarning(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogService?.Write(DiagnosticLogLevel.Error, "Configuración", "No hay permisos para guardar los ajustes.", exception);
            ShowSettingsWarning(exception);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        LogService?.Write(
            DiagnosticLogLevel.Critical,
            "Interfaz",
            "Excepción no controlada en el hilo de la interfaz.",
            e.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? exception = e.ExceptionObject as Exception;
        LogService?.Write(
            DiagnosticLogLevel.Critical,
            "Proceso",
            $"Excepción no controlada. IsTerminating={e.IsTerminating}.",
            exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogService?.Write(
            DiagnosticLogLevel.Error,
            "Tarea",
            "Una tarea terminó con una excepción no observada.",
            e.Exception);
    }

    private void ShowSettingsWarning(Exception exception)
    {
        MessageBox.Show(
            $"No se pudieron cargar o guardar los ajustes de GDM. La aplicación continuará con valores predeterminados.\n\n{exception.Message}",
            "GDM — Ajustes no disponibles",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
