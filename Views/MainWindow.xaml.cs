using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using GDM.Models;
using GDM.Services.Abstractions;
using GDM.ViewModels;


namespace GDM.Views;

public partial class MainWindow : Window
{
    private const int SwRestore = 9;
    private const uint FlashwAll = 3;
    private const uint FlashwTimerNoForeground = 12;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private readonly MainWindowViewModel _viewModel;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _trayIconImage;
    private ListBoxItem? _pressedGamepadItem;
    private bool _isExitRequested;
    private bool _activationRequested;
    public bool IsElevatedRelaunch { get; private set; }
    public bool IsExitRequested => _isExitRequested;

    public MainWindow(
        AppSettings settings,
        IGamepadDetectionService gamepadDetectionService,
        IAppLogService appLogService,
        IControllerRepository controllerRepository,
        SettingsViewModel settingsViewModel,
        IProfileRepository profileRepository,
        IVirtualGamepadService virtualGamepadService,
        ConnectedGamepad? startupVirtualGamepad = null,
        IHidHideService? hidHideService = null,
        bool startMinimized = false,
        Func<string>? defaultVirtualGamepadType = null,
        Func<Guid?>? defaultProfileId = null,
        IGlobalPresetRepository? globalPresetRepository = null)
    {
        InitializeComponent();
        if (startMinimized)
        {
            Opacity = 0;
            ShowInTaskbar = false;
            ShowActivated = false;
        }

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        var trayMenu = new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add("Abrir GDM", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Cerrar GDM", null, (_, _) => RequestExit());
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo localizar el icono del ejecutable de GDM.");
        _trayIconImage = System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            ?? throw new InvalidOperationException("No se pudo cargar el icono de GDM para la bandeja.");
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "GDM — Gamepad Device Manager",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayIcon.BalloonTipClicked += (_, _) => ActivateFromExternalRequest();

        _viewModel = new MainWindowViewModel(
            gamepadDetectionService,
            appLogService,
            controllerRepository,
            record => AppDialog.Confirm(
                "Eliminar mando guardado",
                $"Se eliminará «{record.DisplayName}» y su configuración guardada en GDM. " +
                "El emparejamiento de Windows no se modificará.\n\n¿Deseas continuar?",
                AppDialogIcon.Warning,
                this),
            settingsViewModel,
            profileRepository: profileRepository,
            virtualGamepadService: virtualGamepadService,
            hidHideService: hidHideService,
            requestElevatedVirtualGamepad: LaunchElevatedVirtualGamepad,
            defaultVirtualGamepadType: defaultVirtualGamepadType,
            defaultProfileId: defaultProfileId,
            globalPresetRepository: globalPresetRepository,
            confirmDeleteProfile: profile => AppDialog.Confirm(
                "Eliminar perfil",
                $"Se eliminará el perfil «{profile.DisplayName}». Esta acción no se puede deshacer.\n\n¿Deseas continuar?",
                AppDialogIcon.Warning,
                this),
            trayBalloon: ShowTrayBalloon);
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            if (startupVirtualGamepad is not null)
            {
                await _viewModel.StartVirtualGamepadOnStartupAsync(startupVirtualGamepad);
            }

            await _viewModel.CompleteStartupAsync();
            if (startMinimized && !_activationRequested)
            {
                HideToTray();
            }
        };
        Closed += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIconImage.Dispose();
            _viewModel.Dispose();
            gamepadDetectionService.Dispose();
        };
    }

    public Task RestoreHidHideBeforeCloseAsync() =>
        _viewModel.RestoreHidHideBeforeCloseAsync();

    public Task NotifySettingsAppliedAsync(AppSettings previous, AppSettings current) =>
        _viewModel.HandleSettingsAppliedAsync(previous, current);

    public void HideToTray() => Hide();

    /// <summary>
    /// Muestra una notificación como globo del icono de bandeja. Se usa como
    /// alternativa a los toast cuando GDM se ejecuta elevado, ya que Windows
    /// no muestra notificaciones toast de procesos con privilegios de admin.
    /// </summary>
    private void ShowTrayBalloon(string title, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ShowTrayBalloon(title, text));
            return;
        }

        _trayIcon.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void ActivateFromExternalRequest()
    {
        _activationRequested = true;
        if (Dispatcher.CheckAccess())
        {
            ShowFromTrayImmediately();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(ShowFromTrayImmediately);
        }
    }

    public void RequestExit()
    {
        _isExitRequested = true;
        Close();
    }

    public void MarkExitRequested()
    {
        _isExitRequested = true;
    }

    public void CancelExitRequest()
    {
        _isExitRequested = false;
    }

    private void ShowFromTray()
    {
        _ = Dispatcher.BeginInvoke(ShowFromTrayImmediately);
    }

    private void ShowFromTrayImmediately()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        IntPtr windowHandle = new WindowInteropHelper(this).EnsureHandle();
        WindowState = WindowState.Normal;
        if (!IsVisible)
        {
            Show();
        }

        _ = ShowWindowAsync(windowHandle, SwRestore);
        WindowState = WindowState.Normal;
        Activate();
        _ = BringWindowToTop(windowHandle);
        bool foregroundSet = SetForegroundWindow(windowHandle);
        if (!foregroundSet)
        {
            Topmost = true;
            Topmost = false;
            WindowState = WindowState.Normal;
            Activate();
            _ = BringWindowToTop(windowHandle);
            foregroundSet = SetForegroundWindow(windowHandle);
        }

        if (!foregroundSet)
        {
            var flashInfo = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = windowHandle,
                Flags = FlashwAll | FlashwTimerNoForeground,
                Count = uint.MaxValue
            };
            _ = FlashWindowEx(ref flashInfo);
        }
    }

    private bool LaunchElevatedVirtualGamepad(ConnectedGamepad device)
    {
        if (!AppDialog.Confirm(
                "Permisos para el mando virtual",
                "Para crear el mando virtual, Windows debe abrir GDM como administrador. " +
                "En el primer inicio de la emulación, HIDMaestro también instalará su controlador firmado localmente.\n\n" +
                "¿Deseas continuar?",
                AppDialogIcon.Warning,
                this))
        {
            return false;
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo localizar el ejecutable actual de GDM.");
        string serializedDevice = Convert.ToBase64String(
            JsonSerializer.SerializeToUtf8Bytes(device));
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("--start-virtual-gamepad");
        startInfo.ArgumentList.Add(serializedDevice);

        try
        {
            using Process? elevatedProcess = Process.Start(startInfo);
            if (elevatedProcess is null)
            {
                throw new InvalidOperationException("Windows no pudo volver a iniciar GDM como administrador.");
            }

            IsElevatedRelaunch = true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return false;
        }

        _ = Dispatcher.BeginInvoke(new Action(RequestExit));
        return true;
    }

    private void GamepadListPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        _pressedGamepadItem = FindGamepadItem(listBox, e.OriginalSource as DependencyObject);
        if (_pressedGamepadItem is not null)
        {
            e.Handled = true;
        }
    }

    private void GamepadListPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        ListBoxItem? pressedItem = _pressedGamepadItem;
        _pressedGamepadItem = null;
        if (pressedItem is null)
        {
            return;
        }

        DependencyObject? hitTarget = listBox.InputHitTest(e.GetPosition(listBox)) as DependencyObject;
        ListBoxItem? releasedItem = FindGamepadItem(listBox, hitTarget);
        if (ReferenceEquals(pressedItem, releasedItem))
        {
            pressedItem.IsSelected = true;
        }

        e.Handled = true;
    }

    private void GamepadListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        ScrollViewer? inner = FindVisualDescendant<ScrollViewer>(listBox);
        if (inner is not null && inner.ExtentHeight > inner.ViewportHeight + 0.5)
        {
            return;
        }

        ScrollViewer? parent = FindVisualAncestor<ScrollViewer>(listBox);
        if (parent is null)
        {
            return;
        }

        if (e.Delta > 0)
        {
            parent.LineUp();
        }
        else if (e.Delta < 0)
        {
            parent.LineDown();
        }

        e.Handled = true;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static ListBoxItem? FindGamepadItem(
        System.Windows.Controls.ListBox listBox,
        DependencyObject? source)
    {
        return source is null
            ? null
            : ItemsControl.ContainerFromElement(listBox, source) as ListBoxItem;
    }

    private void OpenWindowsGameControllers(object sender, RoutedEventArgs e)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "joy.cpl",
                UseShellExecute = true
            });
            if (process is null)
            {
                throw new InvalidOperationException(
                    "Windows no inició la ventana Dispositivos de juego.");
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            AppDialog.ShowError(
                "No se pudo abrir Dispositivos de juego",
                $"No se pudo abrir la ventana Dispositivos de juego de Windows.\n\n{exception.Message}",
                this);
        }
    }
}