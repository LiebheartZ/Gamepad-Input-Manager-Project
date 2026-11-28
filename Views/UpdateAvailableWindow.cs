using System.Windows;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using CornerRadius = System.Windows.CornerRadius;
using FontWeights = System.Windows.FontWeights;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using SizeToContent = System.Windows.SizeToContent;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextWrapping = System.Windows.TextWrapping;
using Thickness = System.Windows.Thickness;

namespace GDM.Views;

public sealed class UpdateAvailableWindow : Window
{
    private readonly CheckBox _dontShowAgainCheckBox;

    public bool InstallRequested { get; private set; }
    public bool DontShowAgain => _dontShowAgainCheckBox.IsChecked == true;

    public UpdateAvailableWindow(string version, string tagName)
    {
        Title = "Actualización disponible";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        Brush panel = FindBrush("PanelBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
        Brush border = FindBrush("BorderBrush", Color.FromRgb(0xD9, 0xE2, 0xEA));
        Brush text = FindBrush("PrimaryTextBrush", Color.FromRgb(0x1C, 0x2A, 0x36));
        Brush secondary = FindBrush("SecondaryTextBrush", Color.FromRgb(0x62, 0x73, 0x83));
        Brush accent = FindBrush("AccentBrush", Color.FromRgb(0x1E, 0x6F, 0xB8));

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
            Text = "Hay una nueva build de GDM"
        });
        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = accent,
            FontWeight = FontWeights.SemiBold,
            Text = $"Build {version} ({tagName})"
        });
        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Text = "Puedes descargarla e instalarla ahora. GDM se cerrará al terminar " +
                   "la descarga y se reiniciará actualizado."
        });

        _dontShowAgainCheckBox = new CheckBox
        {
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = text,
            Content = "No volver a mostrar este aviso"
        };
        stack.Children.Add(_dontShowAgainCheckBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var laterButton = new Button
        {
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Ahora no",
            IsCancel = true
        };
        laterButton.Click += (_, _) => { InstallRequested = false; Close(); };
        var installButton = new Button
        {
            MinWidth = 130,
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Descargar e instalar",
            IsDefault = true
        };
        installButton.Click += (_, _) => { InstallRequested = true; Close(); };
        buttons.Children.Add(laterButton);
        buttons.Children.Add(installButton);
        stack.Children.Add(buttons);

        root.Child = stack;
        Content = root;
        Activated += (_, _) => _dontShowAgainCheckBox.Focus();
    }

    public static (bool InstallRequested, bool DontShowAgain) ShowPrompt(
        Window? owner,
        string version,
        string tagName)
    {
        var window = new UpdateAvailableWindow(version, tagName);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        return (window.InstallRequested, window.DontShowAgain);
    }

    private Brush FindBrush(string key, Color fallback)
    {
        if (TryFindResource(key) is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
