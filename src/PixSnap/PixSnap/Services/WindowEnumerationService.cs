using PixSnap.Models;
using System.Collections.Generic;

namespace PixSnap.Services;

public sealed class WindowEnumerationService
{
    private readonly IScreenCaptureService _screenCaptureService;

    public WindowEnumerationService(IScreenCaptureService screenCaptureService)
    {
        _screenCaptureService = screenCaptureService;
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        return _screenCaptureService.GetWindows();
    }
}