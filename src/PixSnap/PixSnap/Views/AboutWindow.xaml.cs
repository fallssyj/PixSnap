using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace PixSnap.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        var viewModel = new AboutViewModel();
        viewModel.RequestClose += Close;

        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
