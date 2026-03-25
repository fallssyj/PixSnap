using PixSnap.ViewModels;
using MicaWPF.Controls;
using System.Windows;
using System.Windows.Input;

namespace PixSnap.Views;

public partial class AboutWindow : MicaWindow
{
    private readonly AboutViewModel _viewModel;

    public AboutWindow()
    {
        _viewModel = new AboutViewModel();
        _viewModel.RequestClose += Close;

        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= Close;
        base.OnClosed(e);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
