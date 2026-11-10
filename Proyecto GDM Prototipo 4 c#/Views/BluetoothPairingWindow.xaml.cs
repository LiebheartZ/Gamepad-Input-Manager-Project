using System.Windows;
using GDM.ViewModels;

namespace GDM.Views;

public partial class BluetoothPairingWindow : Window
{
    public BluetoothPairingWindow(BluetoothPairingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
