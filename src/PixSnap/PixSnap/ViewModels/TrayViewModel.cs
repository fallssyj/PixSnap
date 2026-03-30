using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Windows;

namespace PixSnap.ViewModels;

public partial class TrayViewModel : ObservableObject
{
    private readonly Action _captureAction;
    private readonly Action _showSettingsAction;
    private readonly Action _showAboutAction;
    private readonly Action _showLogViewerAction;

    public TrayViewModel(Action captureAction, Action showSettingsAction, Action showAboutAction, Action showLogViewerAction)
    {
        _captureAction = captureAction;
        _showSettingsAction = showSettingsAction;
        _showAboutAction = showAboutAction;
        _showLogViewerAction = showLogViewerAction;
    }

    [RelayCommand]
    private void Capture() => _captureAction();

    [RelayCommand]
    private void ShowSettings() => _showSettingsAction();

    [RelayCommand]
    private void ShowAbout() => _showAboutAction();

    [RelayCommand]
    private void ShowLogViewer() => _showLogViewerAction();

    [RelayCommand]
    private void Exit()
    {
        var result = Views.MessageBoxWindow.Show(
            "确定要退出 PixSnap 吗？",
            "退出确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            Application.Current.Shutdown();
    }
}
