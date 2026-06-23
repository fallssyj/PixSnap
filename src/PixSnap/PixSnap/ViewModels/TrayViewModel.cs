using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using System;
using System.Windows;

namespace PixSnap.ViewModels;

public partial class TrayViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly TrayMenuService _trayMenu;

    public TrayViewModel(INavigationService navigation, TrayMenuService trayMenu)
    {
        _navigation = navigation;
        _trayMenu = trayMenu;
    }

    [RelayCommand]
    private void Capture() => RunNavigation(_navigation.StartCapture);

    [RelayCommand]
    private void OpenPreview() => RunNavigation(_navigation.OpenScreenshotPreview);

    [RelayCommand]
    private void ShowSettings() => RunNavigation(_navigation.ShowSettings);

    [RelayCommand]
    private void ShowAbout() => RunNavigation(_navigation.ShowAbout);

    [RelayCommand]
    private void ShowLogViewer() => RunNavigation(_navigation.ShowLogViewer);

    [RelayCommand]
    private void Exit()
    {
        _trayMenu.Close();

        var result = AppMessageBox.Show(
            "确定要退出 PixSnap 吗？",
            "退出确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            _navigation.ShutdownApplication();
    }

    private void RunNavigation(Action action)
    {
        _trayMenu.Close();
        action();
    }
}
