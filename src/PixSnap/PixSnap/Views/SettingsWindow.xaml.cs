using PixSnap.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace PixSnap.Views;

public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.RequestClose += Close;

        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ViewModel.RevertUnsavedTheme();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= Close;
        base.OnClosed(e);
    }
}
