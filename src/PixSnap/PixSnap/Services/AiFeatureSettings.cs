using PixSnap.Models;

namespace PixSnap.Services;

/// <summary>AI 功能所用模型偏好（设置页保存后生效）。</summary>
public static class AiFeatureSettings
{
    private static MattingModel _matting = MattingModel.Rmbg14;
    private static SuperResolutionModel _superResolution = SuperResolutionModel.X4;

    public static MattingModel Matting => _matting;
    public static SuperResolutionModel SuperResolution => _superResolution;

    public static void LoadFromSettings()
    {
        _matting = SettingsService.ReadMattingModel();
        _superResolution = SettingsService.ReadSuperResolutionModel();
    }

    public static void Apply(MattingModel matting, SuperResolutionModel superResolution)
    {
        _matting = matting;
        _superResolution = superResolution;
    }
}
