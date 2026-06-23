using Microsoft.Extensions.DependencyInjection;
using PixSnap.Models;
using PixSnap.ViewModels;
using PixSnap.Views;
using System.Windows;

namespace PixSnap.Services;

/// <summary>集中管理截图预览窗口的打开与复用（ViewModel 为单例，窗口应唯一）。</summary>
public sealed class ScreenshotPreviewWindowService
{
    private readonly IServiceProvider _services;

    public ScreenshotPreviewWindowService(IServiceProvider services) => _services = services;

    /// <param name="captured">非 null 时先向单例 ViewModel 加载截图。</param>
    public void Open(ScreenshotCapturedMessage? captured = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Open(captured));
            return;
        }

        OpenCore(captured);
    }

    private void OpenCore(ScreenshotCapturedMessage? captured)
    {
        var viewModel = _services.GetRequiredService<ScreenshotPreviewViewModel>();
        viewModel.BeginPreviewSession();
        if (captured is not null)
            viewModel.Receive(captured);

        foreach (Window window in Application.Current.Windows)
        {
            if (window is ScreenshotPreviewWindow previewWindow)
            {
                if (!previewWindow.IsVisible)
                    previewWindow.Show();

                if (previewWindow.WindowState == WindowState.Minimized)
                    previewWindow.WindowState = WindowState.Normal;

                previewWindow.Activate();
                return;
            }
        }

        var newPreviewWindow = new ScreenshotPreviewWindow
        {
            DataContext = viewModel,
            Topmost = false
        };

        newPreviewWindow.Show();
        newPreviewWindow.Activate();
    }
}
