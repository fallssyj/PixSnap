using MicaWPF.Controls;
using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace PixSnap.Views;

public partial class LogViewerWindow : MicaWindow
{
    private readonly LogViewerViewModel _viewModel;

    public LogViewerWindow()
    {
        _viewModel = new LogViewerViewModel();
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

    private void OnLogTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LogDateGroup { FilePath: not null } group)
            _viewModel.SelectLogFile(group.FilePath);
    }
}
