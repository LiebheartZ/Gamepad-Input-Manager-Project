using GDM.Models;
using GDM.Services;
using GDM.Services.Abstractions;
using GDM.Services.Persistence;
using GDM.Views;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GDM;

public partial class App : System.Windows.Application
{
    public IStoragePathService StoragePaths { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = new();
    public IAppLogService? LogService { get; private set; }
    private IAppSettingsRepository? _settingsRepository;
    private bool _isSavingWindowSize;
    private bool _allowWindowClose;
    private bool _settingsLoadedSuccessfully;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StoragePaths = new LocalStoragePathService();
        var logService = new AppLogService(StoragePaths);
        LogService = logService;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        logService.Write(DiagnosticLogLevel.Information, "GDM", "Inicio de la aplicación.");
        _settingsRepository = new JsonAppSettingsRepository(StoragePaths);

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

        var gamepadDetectionService = new Sdl2GamepadDetectionService();
        var window = new MainWindow(Settings, gamepadDetectionService, logService);
        window.Closing += SaveWindowSizeOnClosing;
        MainWindow = window;
        window.Show();
    }

    private async void SaveWindowSizeOnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose)
        {
            return;
        }

        if (!_settingsLoadedSuccessfully || sender is not Window window)
        {
            return;
        }

        e.Cancel = true;
        if (_isSavingWindowSize || _settingsRepository is null)
        {
            return;
        }

        _isSavingWindowSize = true;

        try
        {
            Rect bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;

            Settings = Settings with
            {
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height
            };

            await _settingsRepository.SaveAsync(Settings);
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
        finally
        {
            _allowWindowClose = true;
            window.Close();
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
