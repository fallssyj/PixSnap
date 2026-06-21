using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using PixSnap.Models;
using PixSnap.ViewModels;
using PixSnap.Views;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;

namespace PixSnap.Services;

/// <summary>处理导航消息，集中管理窗口打开与全局快捷键注册。</summary>
public sealed class NavigationMessageHandler :
    IRecipient<ShowSettingsMessage>,
    IRecipient<ShowLogViewerMessage>,
    IRecipient<ShowAboutMessage>,
    IRecipient<StartCaptureMessage>,
    IRecipient<RecaptureMessage>,
    IRecipient<ShutdownApplicationMessage>,
    IRecipient<HotkeyChangedMessage>
{
    private readonly IServiceProvider _services;

    public NavigationMessageHandler(IServiceProvider services)
    {
        _services = services;
    }

    public void Register() => WeakReferenceMessenger.Default.RegisterAll(this);

    public void Unregister() => WeakReferenceMessenger.Default.UnregisterAll(this);

    public void Receive(ShowSettingsMessage message) => ShowSettings();

    public void Receive(ShowLogViewerMessage message) => ShowLogViewer();

    public void Receive(ShowAboutMessage message) => ShowAbout();

    public void Receive(StartCaptureMessage message) => StartCapture();

    public void Receive(RecaptureMessage message) => Recapture();

    public void Receive(ShutdownApplicationMessage message) => ShutdownApplication();

    public void Receive(HotkeyChangedMessage message) => OnHotkeyChanged(message);

    private void ShowSettings()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is SettingsWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var viewModel = _services.GetRequiredService<SettingsViewModel>();
        var win = new SettingsWindow(viewModel);
        win.ShowDialog();
    }

    private void ShowAbout()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is AboutWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var window = new AboutWindow();
        var owner = Application.Current.MainWindow;
        if (owner?.IsVisible == true)
            window.Owner = owner;
        window.ShowDialog();
    }

    private void ShowLogViewer()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is LogViewerWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        new LogViewerWindow().Show();
    }

    private void StartCapture()
    {
        if (Application.Current.MainWindow?.DataContext is not MainViewModel viewModel)
            return;

        if (viewModel.StartCaptureCommand.CanExecute(null))
            viewModel.StartCaptureCommand.Execute(null);
    }

    private void Recapture()
    {
        foreach (var preview in Application.Current.Windows.OfType<ScreenshotPreviewWindow>().ToList())
            preview.Close();

        StartCapture();
    }

    private void ShutdownApplication() => Application.Current.Shutdown();

    private void OnHotkeyChanged(HotkeyChangedMessage message)
    {
        var hotkeyService = _services.GetRequiredService<GlobalHotkeyService>();
        var navigation = _services.GetRequiredService<INavigationService>();

        hotkeyService.Unregister();
        if (message.Key == Key.None)
            return;

        if (!hotkeyService.Register(message.Modifiers, message.Key, navigation.StartCapture))
        {
            AppMessageBox.Show(
                string.Format(
                    "全局快捷键 {0} 注册失败，可能已被其他程序占用。\n请更换其他快捷键。",
                    HotkeyDisplayFormatter.FormatCompact(message.Modifiers, message.Key)),
                "快捷键注册失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
