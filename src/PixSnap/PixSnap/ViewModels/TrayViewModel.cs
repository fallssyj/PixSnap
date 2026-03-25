using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Windows;

namespace PixSnap.ViewModels;

public partial class TrayViewModel : ObservableObject
{
    private readonly Action _captureAction;
    private readonly Action<int> _delayCaptureAction;
    private readonly Action _captureLastRegionAction;
    private readonly Action _showSettingsAction;
    private readonly Action _showAboutAction;

    public TrayViewModel(Action captureAction, Action<int> delayCaptureAction, Action captureLastRegionAction, Action showSettingsAction, Action showAboutAction)
    {
        _captureAction = captureAction;
        _delayCaptureAction = delayCaptureAction;
        _captureLastRegionAction = captureLastRegionAction;
        _showSettingsAction = showSettingsAction;
        _showAboutAction = showAboutAction;
    }

    [RelayCommand]
    private void Capture() => _captureAction();

    [RelayCommand]
    private void CaptureLastRegion() => _captureLastRegionAction();

    [RelayCommand]
    private void DelayCapture3() => _delayCaptureAction(3);

    [RelayCommand]
    private void DelayCapture5() => _delayCaptureAction(5);

    [RelayCommand]
    private void DelayCapture10() => _delayCaptureAction(10);

    [RelayCommand]
    private void ShowSettings() => _showSettingsAction();

    [RelayCommand]
    private void ShowAbout() => _showAboutAction();

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();
}
