using CommunityToolkit.Mvvm.Messaging;
using PixSnap.Models;

namespace PixSnap.Services;

public sealed class NavigationService : INavigationService
{
    public void ShowSettings() => WeakReferenceMessenger.Default.Send(new ShowSettingsMessage());

    public void ShowLogViewer() => WeakReferenceMessenger.Default.Send(new ShowLogViewerMessage());

    public void ShowAbout() => WeakReferenceMessenger.Default.Send(new ShowAboutMessage());

    public void StartCapture() => WeakReferenceMessenger.Default.Send(new StartCaptureMessage());

    public void OpenScreenshotPreview() => WeakReferenceMessenger.Default.Send(new OpenScreenshotPreviewMessage());

    public void ShutdownApplication() => WeakReferenceMessenger.Default.Send(new ShutdownApplicationMessage());
}
