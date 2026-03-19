using PixSnap.ViewModels;
using PixSnap.Views;
using System.Windows;

namespace PixSnap.Services;

public interface IPreviewWindowService
{
    void ShowPreview(ScreenshotPreviewViewModel viewModel, Window? owner = null);
}

public sealed class PreviewWindowService : IPreviewWindowService
{
    public void ShowPreview(ScreenshotPreviewViewModel viewModel, Window? owner = null)
    {
        var window = new ScreenshotPreviewWindow
        {
            DataContext = viewModel,
            Owner = owner
        };

        window.Show();
        window.Activate();
    }
}