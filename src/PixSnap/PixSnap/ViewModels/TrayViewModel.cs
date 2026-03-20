using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Windows;

namespace PixSnap.ViewModels;

public partial class TrayViewModel : ObservableObject
{
    private readonly Action _captureAction;
    private readonly Action _showAboutAction;

    [ObservableProperty]
    private bool _isStartupEnabled;

    public TrayViewModel(Action captureAction, Action showAboutAction)
    {
        _captureAction = captureAction;
        _showAboutAction = showAboutAction;
        _isStartupEnabled = ReadStartupRegistry();
    }

    [RelayCommand]
    private void Capture() => _captureAction();

    partial void OnIsStartupEnabledChanged(bool value) => WriteStartupRegistry(value);

    [RelayCommand]
    private void ShowAbout() => _showAboutAction();

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    private static bool ReadStartupRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
        return key?.GetValue("PixSnap") is not null;
    }

    private static void WriteStartupRegistry(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enable)
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
                key?.SetValue("PixSnap", $"\"{exePath}\"");
        }
        else
        {
            key?.DeleteValue("PixSnap", throwOnMissingValue: false);
        }
    }
}
