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

    public TrayViewModel(Action captureAction, Action showSettingsAction, Action showAboutAction)
    {
        _captureAction = captureAction;
        _showSettingsAction = showSettingsAction;
        _showAboutAction = showAboutAction;
    }

    [RelayCommand]
    private void Capture() => _captureAction();

    [RelayCommand]
    private void ShowSettings() => _showSettingsAction();

    [RelayCommand]
    private void ShowAbout() => _showAboutAction();

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();
}
