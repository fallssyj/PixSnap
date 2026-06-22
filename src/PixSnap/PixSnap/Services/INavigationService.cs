namespace PixSnap.Services;

public interface INavigationService
{
    void ShowSettings();

    void ShowLogViewer();

    void ShowAbout();

    void StartCapture();

    void ShutdownApplication();
}
