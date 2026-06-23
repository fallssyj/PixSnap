using PixSnap.ViewModels;
using System.Windows;

namespace PixSnap.Views;

public partial class LogViewerWindow : Window
{
    private readonly LogViewerViewModel _viewModel;

    public LogViewerWindow()
    {
        InitializeComponent();
        _viewModel = new LogViewerViewModel();
        DataContext = _viewModel;
        _viewModel.RequestScrollToEnd += OnRequestScrollToEnd;
        Closed += OnClosed;
    }

    private void OnRequestScrollToEnd() => LogContentTextBox.ScrollToEnd();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestScrollToEnd -= OnRequestScrollToEnd;
        Closed -= OnClosed;
        _viewModel.Dispose();
    }
}
