using RapidOCRLib;

namespace PixSnap.Services;

/// <summary>AI 推理（DirectML / CPU）设备偏好，供 OnnxSessionFactory 与 RapidOCR 共用。</summary>
public static class AiGpuSettings
{
    public const int AutoDeviceId = -2;
    public const int CpuOnlyDeviceId = -1;

    private static int _selectedDeviceId = AutoDeviceId;

    public static int SelectedDeviceId => _selectedDeviceId;

    public static void LoadFromSettings()
    {
        _selectedDeviceId = SettingsService.ReadAiGpuDeviceId();
        SyncToOcrRuntime();
    }

    public static void Apply(int deviceId)
    {
        _selectedDeviceId = deviceId;
        SyncToOcrRuntime();
        InvalidateInferenceSessions();
    }

    public static bool ShouldUseDirectMl => _selectedDeviceId != CpuOnlyDeviceId;

    public static IEnumerable<int> GetDirectMlDeviceCandidates()
    {
        if (_selectedDeviceId == CpuOnlyDeviceId)
            yield break;

        if (_selectedDeviceId >= 0)
        {
            yield return _selectedDeviceId;
            yield break;
        }

        foreach (int deviceId in DirectMlDeviceEnumerator.GetPreferredAutoDeviceOrder())
            yield return deviceId;
    }

    private static void SyncToOcrRuntime()
    {
        OnnxSessionHelper.PreferDirectMl = ShouldUseDirectMl;
        OnnxSessionHelper.AutoDeviceOrderProvider = DirectMlDeviceEnumerator.GetPreferredAutoDeviceOrder;
        OnnxSessionHelper.DirectMlDeviceId = _selectedDeviceId switch
        {
            CpuOnlyDeviceId => -1,
            AutoDeviceId => null,
            _ => _selectedDeviceId
        };
    }

    private static void InvalidateInferenceSessions()
    {
        OnnxSessionFactory.InvalidateAll();
        OcrService.Shutdown();
    }
}
