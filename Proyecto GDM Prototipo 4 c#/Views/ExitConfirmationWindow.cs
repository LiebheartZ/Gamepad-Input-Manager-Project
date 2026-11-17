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

public sealed class ExitConfirmationWindow : Window
{
    private readonly CheckBox _dontShowAgainCheckBox;

    public bool Confirmed { get; private set; }
    public bool DontShowAgain => _dontShowAgainCheckBox.IsChecked == true;

    public ExitConfirmationWindow()
    {
        Title = "Cerrar GDM";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        Brush panel = FindBrush("PanelBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
        Brush border = FindBrush("BorderBrush", Color.FromRgb(0xD9, 0xE2, 0xEA));
        Brush text = FindBrush("PrimaryTextBrush", Color.FromRgb(0x1C, 0x2A, 0x36));
        Brush secondary = FindBrush("SecondaryTextBrush", Color.FromRgb(0x62, 0x73, 0x83));

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
            Text = "¿Cerrar GDM por completo?"
        });
        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Text = "GDM dejará de ejecutarse en segundo plano. Los mandos que haya ocultado se restaurarán " +
                   "según la configuración de HidHide."
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
        var cancelButton = new Button
        {
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Cancelar",
            IsCancel = true
        };
        cancelButton.Click += (_, _) => { Confirmed = false; Close(); };
        var closeButton = new Button
        {
            MinWidth = 96,
            Padding = new Thickness(12, 7, 12, 7),
            Content = "Cerrar GDM",
            IsDefault = true
        };
        closeButton.Click += (_, _) => { Confirmed = true; Close(); };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(closeButton);
        stack.Children.Add(buttons);

        root.Child = stack;
        Content = root;
        Activated += (_, _) => _dontShowAgainCheckBox.Focus();
    }

    public static (bool Confirmed, bool DontShowAgain) ShowConfirmation()
    {
        var window = new ExitConfirmationWindow();
        window.ShowDialog();
        return (window.Confirmed, window.DontShowAgain);
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
