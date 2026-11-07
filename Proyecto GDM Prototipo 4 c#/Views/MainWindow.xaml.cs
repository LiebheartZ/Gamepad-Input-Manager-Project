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
        IAppLogService appLogService,
        IControllerRepository controllerRepository)
    {
        InitializeComponent();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        var viewModel = new MainWindowViewModel(
            gamepadDetectionService,
            appLogService,
            controllerRepository,
            record => MessageBox.Show(
                this,
                $"Se eliminará «{record.DisplayName}» y su configuración guardada en GDM. " +
                "El emparejamiento de Windows no se modificará.\n\n¿Deseas continuar?",
                "Eliminar mando guardado",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes);
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) =>
        {
            viewModel.Dispose();
            gamepadDetectionService.Dispose();
        };
    }
}