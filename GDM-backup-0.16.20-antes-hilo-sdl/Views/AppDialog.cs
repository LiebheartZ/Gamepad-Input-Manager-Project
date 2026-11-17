using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
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

public enum AppDialogResult
{
    None,
    Ok,
    Yes,
    No
}

public enum AppDialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question
}

public sealed class AppDialog : Window
{
    private AppDialogResult _result;

    private AppDialog(
        string title,
        string message,
        bool yesNo,
        AppDialogIcon icon,
        Window? owner)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        Topmost = true;
        if (owner is not null)
        {
            Owner = owner;
        }

        Brush panel = FindBrush("PanelBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
        Brush border = FindBrush("BorderBrush", Color.FromRgb(0xD9, 0xE2, 0xEA));
        Brush text = FindBrush("PrimaryTextBrush", Color.FromRgb(0x1C, 0x2A, 0x36));
        Brush secondary = FindBrush("SecondaryTextBrush", Color.FromRgb(0x62, 0x73, 0x83));
        Brush accent = FindBrush("AccentBrush", Color.FromRgb(0x1F, 0x6F, 0xB2));

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != AppDialogIcon.None)
        {
            var iconBlock = new Border
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 12, 0),
                CornerRadius = new CornerRadius(17),
                Background = icon == AppDialogIcon.Error
                    ? new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B))
                    : icon == AppDialogIcon.Warning
                        ? new SolidColorBrush(Color.FromRgb(0xD9, 0x7B, 0x00))
                        : icon == AppDialogIcon.Question
                            ? new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xB2))
                            : accent,
                Child = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 17,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Text = icon switch
                    {
                        AppDialogIcon.Error => "✕",
                        AppDialogIcon.Warning => "!",
                        AppDialogIcon.Question => "?",
                        _ => "i"
                    }
                }
            };
            header.Children.Add(iconBlock);
        }

        header.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = text,
            Text = title
        });

        var root = new Border
        {
            Padding = new Thickness(20),
            Background = panel,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock
        {
            Margin = new Thickness(icon == AppDialogIcon.None ? 0 : 46, 10, 0, 0),
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Text = message
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        if (yesNo)
        {
            var noButton = new Button
            {
                MinWidth = 96,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(12, 7, 12, 7),
                Content = "No",
                IsCancel = true
            };
            noButton.Click += (_, _) => { _result = AppDialogResult.No; Close(); };
            var yesButton = new Button
            {
                MinWidth = 96,
                Padding = new Thickness(12, 7, 12, 7),
                Content = "Sí",
                IsDefault = true
            };
            yesButton.Click += (_, _) => { _result = AppDialogResult.Yes; Close(); };
            buttons.Children.Add(noButton);
            buttons.Children.Add(yesButton);
        }
        else
        {
            var okButton = new Button
            {
                MinWidth = 96,
                Padding = new Thickness(12, 7, 12, 7),
                Content = "Aceptar",
                IsDefault = true,
                IsCancel = true
            };
            okButton.Click += (_, _) => { _result = AppDialogResult.Ok; Close(); };
            buttons.Children.Add(okButton);
        }

        stack.Children.Add(buttons);
        root.Child = stack;
        Content = root;
    }

    public static AppDialogResult Show(
        string title,
        string message,
        bool yesNo = false,
        AppDialogIcon icon = AppDialogIcon.Information,
        Window? owner = null)
    {
        var dialog = new AppDialog(title, message, yesNo, icon, owner);
        dialog.ShowDialog();
        return dialog._result;
    }

    public static bool Confirm(
        string title,
        string message,
        AppDialogIcon icon = AppDialogIcon.Question,
        Window? owner = null) =>
        Show(title, message, yesNo: true, icon, owner) == AppDialogResult.Yes;

    public static void ShowInfo(string title, string message, Window? owner = null) =>
        Show(title, message, icon: AppDialogIcon.Information, owner: owner);

    public static void ShowWarning(string title, string message, Window? owner = null) =>
        Show(title, message, icon: AppDialogIcon.Warning, owner: owner);

    public static void ShowError(string title, string message, Window? owner = null) =>
        Show(title, message, icon: AppDialogIcon.Error, owner: owner);

    private Brush FindBrush(string key, Color fallback)
    {
        if (TryFindResource(key) is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
