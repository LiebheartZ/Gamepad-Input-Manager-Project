using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows.Input;
using GDM.Models;
using GDM.Services.Abstractions;

namespace GDM.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IAppSettingsRepository _settingsRepository;
    private readonly IAppLogService? _appLogService;
    private readonly IWindowsStartupService? _windowsStartupService;
    private readonly Action<AppSettings> _applySettings;
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _restoreDefaultsCommand;
    private AppSettings _currentSettings;
    private string _selectedTheme;
    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _startWithWindowsAsTask;
    private string _defaultVirtualGamepadType;
    private string _xbox360ProfileVariant;
    private Guid? _defaultProfileId;
    private bool _restoreGamepadsOnExit = true;
    private bool _showVirtualGamepadsInAllTabs;
    private string _closeButtonBehavior;
    private bool _showExitConfirmation = true;
    private bool _checkForUpdatesOnStartup = true;
    private string _updateChannel = UpdateChannels.Standard;
    private int _selectedCategoryIndex;
    private bool _isBusy;
    private string _statusTitle = "Ajustes cargados";
    private string _statusMessage = "El tema seleccionado se aplica al guardar los cambios.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new("System", "Sistema"),
        new("Light", "Claro"),
        new("Dark", "Oscuro")
    ];

    public IReadOnlyList<VirtualGamepadTypeOption> VirtualGamepadTypeOptions { get; } =
        VirtualGamepadTypes.Options;

    public IReadOnlyList<VirtualGamepadTypeOption> Xbox360VariantOptions { get; } =
        VirtualGamepadTypes.Xbox360VariantOptions;

    public IReadOnlyList<ThemeOption> CloseButtonBehaviorOptions { get; } =
    [
        new(CloseButtonBehaviors.HideToTray, "Ocultar en la bandeja"),
        new(CloseButtonBehaviors.Exit, "Cerrar GDM")
    ];

    public IReadOnlyList<ThemeOption> UpdateChannelOptions { get; } =
    [
        new(UpdateChannels.Standard, "Estándar"),
        new(UpdateChannels.Nightly, "Nightly")
    ];

    public IReadOnlyList<string> Categories { get; } =
        ["General", "Interfaz", "Mandos", "Dependencias", "Acerca de", "Actualizaciones", "Debug"];

    public ICommand SaveCommand => _saveCommand;
    public ICommand RestoreDefaultsCommand => _restoreDefaultsCommand;

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                _saveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public bool StartWithWindowsAsTask
    {
        get => _startWithWindowsAsTask;
        set => SetProperty(ref _startWithWindowsAsTask, value);
    }

    public string DefaultVirtualGamepadType
    {
        get => _defaultVirtualGamepadType;
        set => SetProperty(ref _defaultVirtualGamepadType, value);
    }

    public string Xbox360ProfileVariant
    {
        get => _xbox360ProfileVariant;
        set => SetProperty(ref _xbox360ProfileVariant, value);
    }

    public Guid? DefaultProfileId
    {
        get => _defaultProfileId;
        set => SetProperty(ref _defaultProfileId, value);
    }

    public bool RestoreGamepadsOnExit
    {
        get => _restoreGamepadsOnExit;
        set => SetProperty(ref _restoreGamepadsOnExit, value);
    }

    public bool ShowVirtualGamepadsInAllTabs
    {
        get => _showVirtualGamepadsInAllTabs;
        set => SetProperty(ref _showVirtualGamepadsInAllTabs, value);
    }

    public string CloseButtonBehavior
    {
        get => _closeButtonBehavior;
        set => SetProperty(ref _closeButtonBehavior, value);
    }

    public bool ShowExitConfirmation
    {
        get => _showExitConfirmation;
        set => SetProperty(ref _showExitConfirmation, value);
    }

    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => SetProperty(ref _checkForUpdatesOnStartup, value);
    }

    public string UpdateChannel
    {
        get => _updateChannel;
        set => SetProperty(ref _updateChannel, value);
    }

    public int SelectedCategoryIndex
    {
        get => _selectedCategoryIndex;
        set
        {
            if (!SetProperty(ref _selectedCategoryIndex, Math.Clamp(value, 0, Categories.Count - 1)))
            {
                return;
            }

            OnPropertyChanged(nameof(IsGeneralCategory));
            OnPropertyChanged(nameof(IsInterfaceCategory));
            OnPropertyChanged(nameof(IsControllersCategory));
            OnPropertyChanged(nameof(IsDependenciesCategory));
            OnPropertyChanged(nameof(IsAboutCategory));
            OnPropertyChanged(nameof(IsUpdatesCategory));
            OnPropertyChanged(nameof(IsDebugCategory));
        }
    }

    public bool IsGeneralCategory => SelectedCategoryIndex == 0;
    public bool IsInterfaceCategory => SelectedCategoryIndex == 1;
    public bool IsControllersCategory => SelectedCategoryIndex == 2;
    public bool IsDependenciesCategory => SelectedCategoryIndex == 3;
    public bool IsAboutCategory => SelectedCategoryIndex == 4;
    public bool IsUpdatesCategory => SelectedCategoryIndex == 5;
    public bool IsDebugCategory => SelectedCategoryIndex == 6;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(SaveButtonText));
                _saveCommand.NotifyCanExecuteChanged();
                _restoreDefaultsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SaveButtonText => IsBusy ? "Guardando..." : "Guardar ajustes";

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

    public SettingsViewModel(
        AppSettings settings,
        IAppSettingsRepository settingsRepository,
        Action<AppSettings> applySettings,
        IAppLogService? appLogService = null,
        IWindowsStartupService? windowsStartupService = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
        _appLogService = appLogService;
        _windowsStartupService = windowsStartupService;
        _currentSettings = settings;
        _selectedTheme = settings.Theme;
        _startWithWindows = settings.StartWithWindows;
        _startMinimized = settings.StartMinimized;
        _startWithWindowsAsTask = settings.StartWithWindowsAsTask;
        _defaultVirtualGamepadType = VirtualGamepadTypes.IsSupported(settings.DefaultVirtualGamepadType)
            ? settings.DefaultVirtualGamepadType
            : VirtualGamepadTypes.Default;
        _xbox360ProfileVariant = VirtualGamepadTypes.NormalizeXbox360Variant(
            settings.Xbox360ProfileVariant);
        _defaultProfileId = settings.DefaultProfileId;
        _restoreGamepadsOnExit = settings.RestoreGamepadsOnExit;
        _showVirtualGamepadsInAllTabs = settings.ShowVirtualGamepadsInAllTabs;
        _closeButtonBehavior = CloseButtonBehaviors.Normalize(settings.CloseButtonBehavior);
        _showExitConfirmation = settings.ShowExitConfirmation;
        _checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        _updateChannel = UpdateChannels.Normalize(settings.UpdateChannel);

        _saveCommand = new AsyncCommand(SaveAsync, () => !IsBusy);
        _restoreDefaultsCommand = new AsyncCommand(RestoreDefaultsAsync, () => !IsBusy);
    }

    public async Task SetDefaultProfileIdAsync(Guid? profileId)
    {
        if (IsBusy)
        {
            return;
        }

        var settings = _currentSettings with
        {
            DefaultProfileId = profileId == Guid.Empty ? null : profileId
        };
        await _settingsRepository.SaveAsync(settings);
        _currentSettings = settings;
        DefaultProfileId = settings.DefaultProfileId;
        _applySettings(settings);
    }

    public async Task SetShowExitConfirmationAsync(bool showExitConfirmation)
    {
        if (IsBusy)
        {
            return;
        }

        var settings = _currentSettings with
        {
            ShowExitConfirmation = showExitConfirmation
        };
        await _settingsRepository.SaveAsync(settings);
        _currentSettings = settings;
        ShowExitConfirmation = settings.ShowExitConfirmation;
        _applySettings(settings);
    }

    public Task SaveAsync() => PersistAsync(
        _currentSettings with
        {
            Theme = SelectedTheme,
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
            StartWithWindowsAsTask = StartWithWindowsAsTask,
            DefaultVirtualGamepadType = VirtualGamepadTypes.IsSupported(DefaultVirtualGamepadType)
                ? DefaultVirtualGamepadType
                : VirtualGamepadTypes.Default,
            Xbox360ProfileVariant = VirtualGamepadTypes.NormalizeXbox360Variant(Xbox360ProfileVariant),
            DefaultProfileId = DefaultProfileId == Guid.Empty ? null : DefaultProfileId,
            RestoreGamepadsOnExit = RestoreGamepadsOnExit,
            ShowVirtualGamepadsInAllTabs = ShowVirtualGamepadsInAllTabs,
            CloseButtonBehavior = CloseButtonBehaviors.Normalize(CloseButtonBehavior),
            ShowExitConfirmation = ShowExitConfirmation,
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartup,
            UpdateChannel = UpdateChannels.Normalize(UpdateChannel)
        },
        "Se guardaron los ajustes.",
        "Ajustes guardados");

    public Task RestoreDefaultsAsync()
    {
        var defaults = new AppSettings
        {
            StartWithWindows = false,
            StartMinimized = false,
            StartWithWindowsAsTask = false,
            WindowWidth = _currentSettings.WindowWidth,
            WindowHeight = _currentSettings.WindowHeight
        };
        return PersistAsync(
            defaults,
            "Se restauraron los ajustes predeterminados. Los mandos guardados no se modificaron.",
            "Ajustes predeterminados restaurados");
    }

    private async Task PersistAsync(AppSettings settings, string successMessage, string successTitle)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        bool startupConfigurationChanged =
            settings.StartWithWindows != _currentSettings.StartWithWindows ||
            settings.StartMinimized != _currentSettings.StartMinimized ||
            settings.StartWithWindowsAsTask != _currentSettings.StartWithWindowsAsTask;
        bool startupConfigurationApplied = false;
        try
        {
            if (startupConfigurationChanged && _windowsStartupService is not null)
            {
                await _windowsStartupService.ConfigureAsync(
                    settings.StartWithWindows,
                    settings.StartMinimized,
                    settings.StartWithWindowsAsTask);
                startupConfigurationApplied = true;
            }

            await _settingsRepository.SaveAsync(settings);
            _currentSettings = settings;
            SelectedTheme = settings.Theme;
            StartWithWindows = settings.StartWithWindows;
            StartMinimized = settings.StartMinimized;
            StartWithWindowsAsTask = settings.StartWithWindowsAsTask;
            DefaultVirtualGamepadType = settings.DefaultVirtualGamepadType;
            Xbox360ProfileVariant = settings.Xbox360ProfileVariant;
            DefaultProfileId = settings.DefaultProfileId;
            RestoreGamepadsOnExit = settings.RestoreGamepadsOnExit;
            ShowVirtualGamepadsInAllTabs = settings.ShowVirtualGamepadsInAllTabs;
            CloseButtonBehavior = settings.CloseButtonBehavior;
            ShowExitConfirmation = settings.ShowExitConfirmation;
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
            UpdateChannel = settings.UpdateChannel;
            _applySettings(settings);
            StatusTitle = successTitle;
            StatusMessage = successMessage;
            _appLogService?.Write(
                DiagnosticLogLevel.Information,
                "Configuración",
                successMessage);
        }
        catch (InvalidDataException exception)
        {
            await HandleSaveErrorAsync("Los ajustes no superaron la validación", exception, startupConfigurationApplied);
        }
        catch (IOException exception)
        {
            await HandleSaveErrorAsync("No se pudieron guardar los ajustes", exception, startupConfigurationApplied);
        }
        catch (UnauthorizedAccessException exception)
        {
            await HandleSaveErrorAsync("No hay permisos para guardar los ajustes", exception, startupConfigurationApplied);
        }
        catch (SecurityException exception)
        {
            await HandleSaveErrorAsync("Windows no permitió actualizar el inicio automático", exception, startupConfigurationApplied);
        }
        catch (InvalidOperationException exception)
        {
            await HandleSaveErrorAsync("No se pudo actualizar la configuración de inicio de Windows", exception, startupConfigurationApplied);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            await HandleSaveErrorAsync("Windows no pudo actualizar la tarea de inicio de GDM", exception, startupConfigurationApplied);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandleSaveErrorAsync(
        string title,
        Exception exception,
        bool startupConfigurationApplied)
    {
        if (startupConfigurationApplied && _windowsStartupService is not null)
        {
            try
            {
                await _windowsStartupService.ConfigureAsync(
                    _currentSettings.StartWithWindows,
                    _currentSettings.StartMinimized,
                    _currentSettings.StartWithWindowsAsTask);
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException or
                UnauthorizedAccessException or
                SecurityException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                exception = new InvalidOperationException(
                    $"{exception.Message} Además, no se pudo revertir la configuración de inicio anterior.",
                    new AggregateException(exception, rollbackException));
            }
        }

        SetSaveError(title, exception);
    }

    private void SetSaveError(string title, Exception exception)
    {
        StatusTitle = title;
        StatusMessage = $"No se aplicaron los cambios. {exception.Message}";
        _appLogService?.Write(
            DiagnosticLogLevel.Error,
            "Configuración",
            title,
            exception);
    }

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

    public sealed record ThemeOption(string Value, string Label);

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter) => await execute();

        public void NotifyCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
