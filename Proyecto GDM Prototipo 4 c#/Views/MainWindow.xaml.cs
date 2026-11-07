using System.Windows;
using GDM.Models;
using GDM.Services.Abstractions;
using GDM.ViewModels;

namespace GDM.Views;

public partial class MainWindow : Window
{
    public MainWindow(
        AppSettings settings,
        IGamepadDetectionService gamepadDetectionService,
        IAppLogService appLogService)
    {
        InitializeComponent();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        var viewModel = new MainWindowViewModel(gamepadDetectionService, appLogService);
        DataContext = viewModel;
        Closed += (_, _) =>
        {
            viewModel.Dispose();
            gamepadDetectionService.Dispose();
        };
    }
}