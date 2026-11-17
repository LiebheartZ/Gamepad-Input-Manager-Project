using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GDM.Models;
using GDM.Services;
using GDM.Services.Abstractions;
using GDM.Views;

namespace GDM.ViewModels;

public sealed class UpdateViewModel : INotifyPropertyChanged
{
    private readonly IAppUpdateService _updateService;
    private readonly Func<string> _updateChannel;
    private readonly Action _requestInstallExit;
    private readonly IAppLogService? _appLogService;
    private readonly AsyncCommand _checkCommand;
    private readonly AsyncCommand _installCommand;
    private readonly AsyncCommand _forceInstallLatestCommand;
    private AppReleaseInfo? _availableUpdate;
    private bool _isBusy;
    private bool _isDownloading;
    private int _downloadProgress = -1;
    private string _statusTitle = "Actualizaciones";
    private string _statusMessage = "Pulsa «Buscar actualizaciones» para consultar GitHub.";
    private string _lastCheckResult = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RepositoryDisplay => _updateService.RepositoryDisplay;
    public string CurrentVersionLabel { get; } =
        $"Versión instalada: {GetCurrentVersion()}";

    public ICommand CheckCommand => _checkCommand;
    public ICommand InstallCommand => _installCommand;
    public ICommand ForceInstallLatestCommand => _forceInstallLatestCommand;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCheck));
                OnPropertyChanged(nameof(CanInstall));
                _checkCommand.NotifyCanExecuteChanged();
                _installCommand.NotifyCanExecuteChanged();
                _forceInstallLatestCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanCheck => !IsBusy;
    public bool CanInstall => !IsBusy && _availableUpdate?.Package is not null;

    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    public int DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public AppReleaseInfo? AvailableUpdate => _availableUpdate;

    public bool HasUpdateAvailable => _availableUpdate is not null;

    public string UpdateStatusLabel => _availableUpdate is null
        ? _lastCheckResult.Length > 0
            ? _lastCheckResult
            : "No hay información de actualizaciones todavía."
        : $"Nueva versión disponible: {_availableUpdate.Version} ({_availableUpdate.TagName})";

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

    public string ChannelDisplayName =>
        _updateChannel() == UpdateChannels.Nightly ? "Nightly" : "Estándar";

    public UpdateViewModel(
        IAppUpdateService updateService,
        Func<string> updateChannel,
        Action requestInstallExit,
        IAppLogService? appLogService = null)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _updateChannel = updateChannel ?? throw new ArgumentNullException(nameof(updateChannel));
        _requestInstallExit = requestInstallExit ?? throw new ArgumentNullException(nameof(requestInstallExit));
        _appLogService = appLogService;
        _checkCommand = new AsyncCommand(CheckAsync, () => CanCheck);
        _installCommand = new AsyncCommand(InstallAsync, () => CanInstall);
        _forceInstallLatestCommand = new AsyncCommand(ForceInstallLatestAsync, () => CanCheck);
    }

    public Task CheckAsync() => CheckAsync(silent: false);

    public async Task CheckAsync(bool silent)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            AppReleaseInfo? release = await _updateService.GetLatestReleaseAsync(
                UpdateChannels.Normalize(_updateChannel()));
            if (release is null)
            {
                _availableUpdate = null;
                _lastCheckResult = "No se encontró una versión publicada en GitHub.";
                if (!silent)
                {
                    StatusTitle = "Sin actualizaciones";
                    StatusMessage = _lastCheckResult;
                }

                return;
            }

            if (IsNewerVersion(release.Version))
            {
                _availableUpdate = release;
                _lastCheckResult = string.Empty;
                if (!silent)
                {
                    StatusTitle = "Actualización disponible";
                    StatusMessage =
                        $"Hay una nueva versión ({release.Version}). Pulsa «Descargar e instalar» para aplicarla; GDM se cerrará y se reiniciará actualizado.";
                }
            }
            else
            {
                _availableUpdate = null;
                _lastCheckResult = $"Ya tienes la versión más reciente ({GetCurrentVersion()}).";
                if (!silent)
                {
                    StatusTitle = "Sin actualizaciones";
                    StatusMessage = _lastCheckResult;
                }
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            InvalidOperationException or
            IOException)
        {
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Actualizaciones",
                "No se pudo consultar la última versión en GitHub.",
                exception);
            if (!silent)
            {
                StatusTitle = "Error al buscar actualizaciones";
                StatusMessage = $"No se pudo conectar con GitHub: {exception.Message}";
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasUpdateAvailable));
            OnPropertyChanged(nameof(UpdateStatusLabel));
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    private Task InstallAsync() => InstallReleaseAsync(_availableUpdate);

    public async Task ForceInstallLatestAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            AppReleaseInfo? release = await _updateService.GetLatestReleaseAsync(UpdateChannels.Nightly);
            if (release is null)
            {
                StatusTitle = "Sin versiones publicadas";
                StatusMessage = "No se encontró ningún release en GitHub.";
                return;
            }

            _availableUpdate = release;
            OnPropertyChanged(nameof(UpdateStatusLabel));
            OnPropertyChanged(nameof(HasUpdateAvailable));
            await InstallReleaseAsync(release);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            JsonException or
            InvalidOperationException or
            IOException)
        {
            StatusTitle = "No se pudo buscar la última versión";
            StatusMessage = $"No se pudo conectar con GitHub: {exception.Message}";
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Actualizaciones",
                "No se pudo consultar la última versión en GitHub.",
                exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task InstallAvailableUpdateAsync() => InstallReleaseAsync(_availableUpdate, skipConfirmation: true);
    private async Task InstallReleaseAsync(AppReleaseInfo? release, bool skipConfirmation = false)
    {
        if (release?.Package is not { } package)
        {
            StatusTitle = "No hay paquete para instalar";
            StatusMessage = "La versión publicada no incluye un archivo .zip descargable.";
            _appLogService?.Write(
                DiagnosticLogLevel.Warning,
                "Actualizaciones",
                $"El release {(release is null ? "-" : release.TagName)} no tiene un asset .zip; no se puede instalar.");
            Views.AppDialog.ShowWarning(
                "Actualización no disponible",
                "El release publicado no incluye el archivo .zip de la aplicación. Vuelve a publicar el release adjuntando el .zip.");
            return;
        }

        if (!skipConfirmation && !AppDialog.Confirm(
                "Actualizar GDM",
                $"Se descargará GDM {release.Version} desde {RepositoryDisplay} y se instalará sobre esta copia.\n\n" +
                "La aplicación se cerrará al terminar la descarga y se reiniciará actualizada. ¿Continuar?",
                AppDialogIcon.Question))
        {
            return;
        }

        IsBusy = true;
        IsDownloading = true;
        DownloadProgress = 0;
        try
        {
            string stagedDirectory = await _updateService.DownloadPackageAsync(
                package,
                new Progress<int>(value => DownloadProgress = value));
            string scriptPath = UpdateInstaller.PrepareApplyScript(
                stagedDirectory,
                AppContext.BaseDirectory,
                Environment.ProcessId);
            UpdateInstaller.LaunchDetached(scriptPath);
            StatusTitle = "Actualización lista";
            StatusMessage = "GDM se cerrará y se reiniciará con la nueva versión.";
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Actualizaciones",
                $"Actualización {release.Version} preparada en «{stagedDirectory}»; se reiniciará GDM.");
            _requestInstallExit();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            TaskCanceledException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            StatusTitle = "No se pudo instalar la actualización";
            StatusMessage = $"{exception.Message}";
            _appLogService?.Write(
                DiagnosticLogLevel.Error,
                "Actualizaciones",
                "Falló la descarga o preparación de la actualización.",
                exception);
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = -1;
            IsBusy = false;
            _installCommand.NotifyCanExecuteChanged();
        }
    }

    private bool IsNewerVersion(string versionText)
    {
        if (!Version.TryParse(versionText, out Version? remote) ||
            !Version.TryParse(GetCurrentVersion(), out Version? current))
        {
            return false;
        }

        if (_updateChannel() == UpdateChannels.Nightly)
        {
            return remote > current;
        }

        return remote.Major > current.Major ||
            (remote.Major == current.Major && remote.Minor > current.Minor);
    }

    private static string GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
