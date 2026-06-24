using Microsoft.Extensions.DependencyInjection;
using PixSnap.Models;
using PixSnap.ViewModels;
using PixSnap.Views;
using System.Windows;

namespace PixSnap.Services;

/// <summary>管理截图预览窗口：已有图片在编辑时，新截图打开新窗口，避免覆盖当前编辑。</summary>
public sealed class ScreenshotPreviewWindowService
{
    private readonly IServiceProvider _services;

    public ScreenshotPreviewWindowService(IServiceProvider services) => _services = services;

    /// <param name="captured">非 null 时向 ViewModel 加载截图。</param>
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
        // 已有预览窗口正在编辑图片时，新截图单独开窗口，不覆盖当前内容。
        if (captured is not null && HasVisiblePreviewWithImage())
        {
            ShowPreviewWindow(CreateViewModel(captured));
            return;
        }

        var existing = FindPreviewWindow();
        if (existing is not null)
        {
            var viewModel = GetOrCreateViewModel(existing);
            if (existing.DataContext is null)
                existing.DataContext = viewModel;

            viewModel.BeginPreviewSession();
            if (captured is not null)
                viewModel.LoadCapturedScreenshot(captured);

            ActivateWindow(existing);
            return;
        }

        var newViewModel = captured is not null
            ? CreateViewModel(captured)
            : _services.GetRequiredService<ScreenshotPreviewViewModel>();
        newViewModel.BeginPreviewSession();
        ShowPreviewWindow(newViewModel);
    }

    private ScreenshotPreviewViewModel CreateViewModel(ScreenshotCapturedMessage captured)
    {
        var viewModel = _services.GetRequiredService<ScreenshotPreviewViewModel>();
        viewModel.BeginPreviewSession();
        viewModel.LoadCapturedScreenshot(captured);
        return viewModel;
    }

    private ScreenshotPreviewViewModel GetOrCreateViewModel(ScreenshotPreviewWindow window)
        => window.DataContext as ScreenshotPreviewViewModel
           ?? _services.GetRequiredService<ScreenshotPreviewViewModel>();

    private static ScreenshotPreviewWindow? FindPreviewWindow()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is ScreenshotPreviewWindow previewWindow)
                return previewWindow;
        }

        return null;
    }

    private static bool HasVisiblePreviewWithImage()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is ScreenshotPreviewWindow previewWindow
                && previewWindow.IsVisible
                && previewWindow.DataContext is ScreenshotPreviewViewModel viewModel
                && viewModel.ScreenshotImage is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static void ShowPreviewWindow(ScreenshotPreviewViewModel viewModel)
    {
        var window = new ScreenshotPreviewWindow
        {
            DataContext = viewModel,
            Topmost = false
        };

        window.Show();
        window.Activate();
    }

    private static void ActivateWindow(ScreenshotPreviewWindow window)
    {
        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }
}
