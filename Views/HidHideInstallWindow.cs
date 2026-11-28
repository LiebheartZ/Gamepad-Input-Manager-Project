using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GDM.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using FontWeights = System.Windows.FontWeights;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using SizeToContent = System.Windows.SizeToContent;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextWrapping = System.Windows.TextWrapping;
using Thickness = System.Windows.Thickness;

namespace GDM.Views;

public sealed class HidHideInstallWindow : Window
{
    private readonly HidHideInstallerService _installer = new();
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly StackPanel _actionButtons;
    private readonly Button _installButton;
    private readonly Button _cancelButton;
    private readonly Button _closeButton;
    private readonly CancellationTokenSource _downloadCts = new();
    private bool _installing;
    private bool _installerRunning;

    public bool InstallFinished { get; private set; }

    public HidHideInstallWindow()
    {
        Title = "Instalar HidHide";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        Brush panel = FindBrush("PanelBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
        Brush border = FindBrush("BorderBrush", Color.FromRgb(0xD9, 0xE2, 0xEA));
        Brush text = FindBrush("PrimaryTextBrush", Color.FromRgb(0x1C, 0x2A, 0x36));
        Brush secondary = FindBrush("SecondaryTextBrush", Color.FromRgb(0x62, 0x73, 0x83));
        Brush accent = FindBrush("AccentBrush", Color.FromRgb(0x14, 0x77, 0xD4));

        var root = new Border
        {
            Padding = new Thickness(20),
            Background = panel,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = text,
            Text = "Instalar HidHide"
        });
        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Text = "HidHide es el driver que permite a GDM ocultar tus mandos físicos a los juegos " +
                   "mientras GDM los sigue leyendo. La instalación puede pedir reiniciar el equipo."
        });

        var credits = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11,
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap
        };
        credits.Inlines.Add(new Run("HidHide © Eric Korff de Gidts y Benjamin Höglinger-Stelzer — licencia MIT. "));
        credits.Inlines.Add(new Run("Desarrollado por Nefarius Software Solutions e.U.")
        {
            FontWeight = FontWeights.SemiBold
        });
        credits.Inlines.Add(new Run(
            ". GDM descarga el instalador oficial desde el repositorio del proyecto y no está afiliado a él."));
        stack.Children.Add(credits);

        var link = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11,
            Foreground = accent,
            Cursor = System.Windows.Input.Cursors.Hand,
            TextDecorations = TextDecorations.Underline,
            Text = "Página oficial de HidHide",
            ToolTip = _installer.OfficialPage
        };
        link.MouseLeftButtonUp += (_, _) => OpenOfficialPage();
        stack.Children.Add(link);

        _progressBar = new ProgressBar
        {
            Margin = new Thickness(0, 14, 0, 0),
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(_progressBar);

        _statusText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(_statusText);

        _actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        _cancelButton = new Button
        {
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Cancelar",
            IsCancel = true
        };
        _cancelButton.Click += (_, _) => { if (!_installerRunning) { _downloadCts.Cancel(); Close(); } };
        var webButton = new Button
        {
            MinWidth = 130,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Página oficial"
        };
        webButton.Click += (_, _) => OpenOfficialPage();
        _installButton = new Button
        {
            MinWidth = 150,
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Instalar directamente",
            IsDefault = true
        };
        _installButton.Click += async (_, _) => await InstallAsync();
        _closeButton = new Button
        {
            MinWidth = 96,
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Cerrar",
            IsDefault = true,
            Visibility = Visibility.Collapsed
        };
        _closeButton.Click += (_, _) => Close();
        _actionButtons.Children.Add(_cancelButton);
        _actionButtons.Children.Add(webButton);
        _actionButtons.Children.Add(_installButton);
        _actionButtons.Children.Add(_closeButton);
        stack.Children.Add(_actionButtons);

        root.Child = stack;
        Content = root;
        Closing += OnClosing;
    }

    public static bool ShowInstallDialog()
    {
        var window = new HidHideInstallWindow();
        window.ShowDialog();
        return window.InstallFinished;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_installing && !_installerRunning)
        {
            _downloadCts.Cancel();
        }
    }

    private void OpenOfficialPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _installer.OfficialPage,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            SetStatus($"No se pudo abrir la página oficial: {exception.Message}");
        }
    }

    private async Task InstallAsync()
    {
        if (_installing)
        {
            return;
        }

        _installing = true;
        _installButton.IsEnabled = false;
        var webButton = _actionButtons.Children.OfType<Button>().ElementAtOrDefault(1);
        if (webButton is not null)
        {
            webButton.IsEnabled = false;
        }

        string installerPath = Path.Combine(
            Path.GetTempPath(),
            "GDM",
            "HidHide",
            "HidHide-Installer.exe");

        try
        {
            SetStatus("Buscando la última versión de HidHide…");
            ShowProgress();
            HidHideReleaseInfo release = await _installer.GetLatestReleaseAsync(_downloadCts.Token);
            SetStatus($"Descargando {release.ReleaseName} ({FormatSize(release.SizeBytes)})…");

            var progress = new Progress<double>(fraction =>
            {
                _progressBar.Value = fraction;
                SetStatusTextOnly(
                    $"Descargando {release.ReleaseName}… {fraction:P0}");
            });
            await _installer.DownloadAsync(
                release.DownloadUrl,
                installerPath,
                progress,
                _downloadCts.Token);

            SetStatus("Verificando la firma digital del instalador…");
            string publisher = await Task.Run(
                () => HidHideInstallerService.VerifyInstallerSignature(installerPath),
                _downloadCts.Token);
            SetStatus($"Firma verificada: {publisher}. Iniciando el instalador de HidHide…");

            Process installer = HidHideInstallerService.StartInstaller(installerPath);
            _installerRunning = true;
            _cancelButton.IsEnabled = false;
            _progressBar.Visibility = Visibility.Collapsed;
            Topmost = false;
            WindowState = WindowState.Minimized;
            if (System.Windows.Application.Current?.MainWindow is { } mainWindow)
            {
                mainWindow.WindowState = WindowState.Minimized;
            }
            SetStatus(
                "Completa la instalación en la ventana del instalador de HidHide. " +
                "Es posible que Windows solicite reiniciar el equipo al terminar.");

            await Task.Run(() => installer.WaitForExit(), CancellationToken.None);
            HidHideInstallerService.TryDeleteFile(installerPath);
            InstallFinished = installer.ExitCode == 0 || installer.ExitCode == 3010 ||
                installer.ExitCode == 1641;
            _installerRunning = false;
            WindowState = WindowState.Normal;
            Activate();
            if (System.Windows.Application.Current?.MainWindow is { } finishedMainWindow)
            {
                finishedMainWindow.WindowState = WindowState.Normal;
            }

            SetStatus(
                InstallFinished
                    ? "Instalación finalizada. Si Windows pidió reiniciar, reinicia el equipo; " +
                      "GDM detectará HidHide automáticamente."
                    : $"El instalador terminó con código {installer.ExitCode}. " +
                      "Si HidHide sigue sin aparecer, reinicia el equipo e inténtalo de nuevo.");
            _closeButton.Visibility = Visibility.Visible;
            _installButton.Visibility = Visibility.Collapsed;
            if (webButton is not null)
            {
                webButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Descarga cancelada.");
            _installButton.IsEnabled = true;
            if (webButton is not null)
            {
                webButton.IsEnabled = true;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"No se pudo instalar HidHide: {exception.Message}");
            _installButton.IsEnabled = true;
            if (webButton is not null)
            {
                webButton.IsEnabled = true;
            }
        }
        finally
        {
            _installing = false;
        }
    }

    private void ShowProgress()
    {
        _progressBar.Visibility = Visibility.Visible;
        _statusText.Visibility = Visibility.Visible;
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusText.Visibility = Visibility.Visible;
    }

    private void SetStatusTextOnly(string text) => _statusText.Text = text;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.0} MB",
        >= 1_024 => $"{bytes / 1_024.0:0.0} KB",
        _ => $"{bytes} bytes"
    };

    private Brush FindBrush(string key, Color fallback)
    {
        if (TryFindResource(key) is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
