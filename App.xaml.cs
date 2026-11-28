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

namespace GDM;

public partial class App : System.Windows.Application
{
    public IStoragePathService StoragePaths { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = new();
    public IAppLogService? LogService { get; private set; }
    private IAppSettingsRepository? _settingsRepository;
    private bool _isHandlingWindowClose;
    private bool _allowWindowClose;
    private bool _suppressExitConfirmation;
    private bool _settingsLoadedSuccessfully;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private bool _activationPending;
    private GDM.ViewModels.SettingsViewModel? _settingsViewModel;
    private Services.Abstractions.IVirtualGamepadService? _virtualGamepadService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo helper de emparejamiento Bluetooth: GDM.Pair.exe corre sin
        // privilegios (lanzado vía explorer.exe) porque PairAsync falla en
        // procesos elevados en algunos equipos (Windows 10).
        if (GDM.Services.BluetoothPairHelper.IsPairHelperProcess)
        {
            int pairExitCode = await GDM.Services.BluetoothPairHelper.RunAsync();
            Shutdown(pairExitCode);
            return;
        }

        // Modo helper: cuando el ejecutable corre como GDM.Notify.exe (hardlink
        // lanzado por explorer.exe desde la instancia elevada), muestra los
        // toasts pendientes y sale. Se procesa antes del coordinador de
        // instancia única para no chocar con la instancia principal.
        if (ToastNotificationService.IsNotifyHelperProcess)
        {
            int shown = ToastNotificationService.ShowPendingToasts();
            try
            {
                bool elevated = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                string marker = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GDM", "helper-log.txt");
                File.AppendAllText(marker,
                    $"{DateTime.Now:HH:mm:ss.fff} shown={shown} elevado={elevated}" +
                    (ToastNotificationService.LastToastError is { } err ? $" error={err}" : string.Empty) +
                    "\n");
            }
            catch
            {
            }

            Shutdown();
            return;
        }

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
                    AppDialog.ShowError("GDM — Inicio elevado", exception.Message);
                    Shutdown();
                    return;
                }
            }
            else
            {
                AppDialog.ShowInfo(
                    "GDM ya está abierto",
                    "GDM ya se está ejecutando. Se abrirá la ventana de la instancia existente.");

                bool activated = await SingleInstanceCoordinator.NotifyPrimaryAsync();
                if (!activated)
                {
                    AppDialog.ShowWarning(
                        "GDM ya está abierto",
                        "Ya hay una instancia de GDM en ejecución, pero no se pudo activar su ventana. " +
                        "Comprueba que la instancia existente no esté bloqueada.");
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
        ToastNotificationService.CleanupStalePendingToasts();
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
                AppSettings previousSettings = Settings;
                Settings = updatedSettings;
                ApplyTheme(updatedSettings.Theme);
                if (MainWindow is MainWindow window)
                {
                    _ = window.NotifySettingsAppliedAsync(previousSettings, updatedSettings);
                }
            },
            logService,
            windowsStartupService);
        _settingsViewModel = settingsViewModel;
        var gamepadDetectionService = new Sdl2GamepadDetectionService(
            message => logService.Write(
                DiagnosticLogLevel.Information,
                "SDL2 mapeo",
                message));
        var controllerRepository = new JsonControllerRepository(StoragePaths);
        var profileRepository = new JsonProfileRepository(StoragePaths);
        var globalPresetRepository = new JsonGlobalPresetRepository(StoragePaths);
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
            AppDialog.ShowWarning(
                "GDM — Recuperación de HidHide pendiente",
                "GDM no pudo recuperar automáticamente un cambio pendiente de HidHide. " +
                "La operación se volverá a intentar antes de ocultar o restaurar mandos.\n\n" +
                exception.Message);
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
            AppDialog.ShowWarning(
                "GDM — Acceso de HidHide",
                "GDM no pudo preparar su acceso al mando físico antes de iniciar SDL2. " +
                "El mando podría dejar de enviar entradas si se oculta automáticamente.\n\n" +
                exception.Message);
        }

        var virtualGamepadService = new HidMaestroVirtualGamepadService(
            gamepadDetectionService,
            () => Settings.Xbox360ProfileVariant);
        _virtualGamepadService = virtualGamepadService;
        _ = Task.Run(() =>
        {
            bool cleaned = virtualGamepadService.CleanupOrphanedVirtualControllers();
            LogService?.Write(
                cleaned ? DiagnosticLogLevel.Information : DiagnosticLogLevel.Warning,
                "HIDMaestro",
                cleaned
                    ? "Limpieza de mandos virtuales huérfanos completada."
                    : "No se pudieron eliminar los mandos virtuales huérfanos (¿sin permisos de administrador?).");
        });
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
            e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase),
            () => Settings.DefaultVirtualGamepadType,
            () => Settings.DefaultProfileId,
            globalPresetRepository);
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
        _virtualGamepadService?.Dispose();
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
            AppDialog.ShowError("GDM — Inicio de emulación", exception.Message);
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
        AppDialog.ShowError(
            "GDM — Inicio de emulación",
            $"No se pudo iniciar automáticamente el mando virtual.\n\n{exception.Message}");
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

    private void HandleWindowClosing(object? sender, CancelEventArgs e)
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
            if (CloseButtonBehaviors.Normalize(Settings.CloseButtonBehavior) ==
                CloseButtonBehaviors.Exit)
            {
                window.MarkExitRequested();
            }
            else
            {
                window.HideToTray();
                return;
            }
        }
        else
        {
            e.Cancel = true;
        }

        window.Dispatcher.BeginInvoke(
            new Action(() => _ = HandleExitRequestedAsync(window)));
    }

    public void RequestProgrammaticExit(bool skipExitConfirmation = false)
    {
        _suppressExitConfirmation |= skipExitConfirmation;
        if (MainWindow is MainWindow window && !window.IsExitRequested)
        {
            window.MarkExitRequested();
            window.Dispatcher.BeginInvoke(
                new Action(() => _ = HandleExitRequestedAsync(window)));
        }
    }

    private async Task HandleExitRequestedAsync(MainWindow window)
    {
        if (_isHandlingWindowClose)
        {
            return;
        }

        _isHandlingWindowClose = true;

        try
        {
            if (!window.IsElevatedRelaunch &&
                Settings.ShowExitConfirmation &&
                !_suppressExitConfirmation)
            {
                (bool confirmed, bool dontShowAgain) = ExitConfirmationWindow.ShowConfirmation();
                if (!confirmed)
                {
                    window.CancelExitRequest();
                    return;
                }

                if (dontShowAgain)
                {
                    await PersistShowExitConfirmationAsync(false);
                }
            }

            if (!window.IsElevatedRelaunch)
            {
                try
                {
                    if (_virtualGamepadService is not null)
                    {
                        Task stopOutputsTask = _virtualGamepadService.StopAllAsync();
                        await Task.WhenAny(stopOutputsTask, Task.Delay(TimeSpan.FromSeconds(8)));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or
                    InvalidOperationException or
                    Win32Exception or
                    ObjectDisposedException)
                {
                    LogService?.Write(
                        DiagnosticLogLevel.Warning,
                        "Salida virtual",
                        "No se pudieron detener todas las salidas virtuales antes de cerrar.",
                        exception);
                }

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
                    if (!AppDialog.Confirm(
                            "GDM — No se pudo restaurar HidHide",
                            "No se pudo restaurar la lista negra de HidHide. Si cierras GDM, los mandos afectados pueden permanecer ocultos " +
                            "hasta que GDM vuelva a iniciarse; el diario se conservará para reintentar la restauración.\n\n" +
                            $"{exception.Message}\n\n¿Deseas cerrar GDM de todos modos?",
                            AppDialogIcon.Warning,
                            window))
                    {
                        window.CancelExitRequest();
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
        catch (Exception exception)
        {
            window.CancelExitRequest();
            LogService?.Write(
                DiagnosticLogLevel.Error,
                "Cierre",
                "No se pudo completar la solicitud de cierre de GDM.",
                exception);
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

    private async Task PersistShowExitConfirmationAsync(bool showExitConfirmation)
    {
        try
        {
            if (_settingsViewModel is not null)
            {
                await _settingsViewModel.SetShowExitConfirmationAsync(showExitConfirmation);
                return;
            }

            Settings = Settings with { ShowExitConfirmation = showExitConfirmation };
            if (_settingsRepository is not null)
            {
                await _settingsRepository.SaveAsync(Settings);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            LogService?.Write(
                DiagnosticLogLevel.Warning,
                "Configuración",
                "No se pudo guardar la preferencia del aviso de cierre.",
                exception);
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
        AppDialog.ShowWarning(
            "GDM — Ajustes no disponibles",
            $"No se pudieron cargar o guardar los ajustes de GDM. La aplicación continuará con valores predeterminados.\n\n{exception.Message}");
    }
}
