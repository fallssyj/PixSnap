using PixSnap.ViewModels;
using PixSnap.Views;

namespace PixSnap.Services;

public interface IPreviewWindowService
{
    void ShowPreview(ScreenshotPreviewViewModel viewModel);
}

public sealed class PreviewWindowService : IPreviewWindowService
{
    public void ShowPreview(ScreenshotPreviewViewModel viewModel)
    {
        var window = new ScreenshotPreviewWindow
        {
            DataContext = viewModel
        };

        window.Show();
        window.Activate();
    }
}