using PixSnap.Models;

namespace PixSnap.Services;

/// <summary>AI 功能所用模型偏好（设置页保存后生效）。</summary>
public static class AiFeatureSettings
{
    private static MattingModel _matting = MattingModel.Rmbg14;
    private static SuperResolutionModel _superResolution = SuperResolutionModel.X4;
    private static SegmentationModel _segmentation = SegmentationModel.MobileSam;
    private static VisionModel _vision = VisionModel.Florence2;

    public static MattingModel Matting => _matting;
    public static SuperResolutionModel SuperResolution => _superResolution;
    public static SegmentationModel Segmentation => _segmentation;
    public static VisionModel Vision => _vision;

    public static void LoadFromSettings()
    {
        _matting = SettingsService.ReadMattingModel();
        _superResolution = SettingsService.ReadSuperResolutionModel();
        _segmentation = SettingsService.ReadSegmentationModel();
        _vision = SettingsService.ReadVisionModel();
    }

    public static void Apply(MattingModel matting, SuperResolutionModel superResolution, SegmentationModel segmentation, VisionModel vision)
    {
        _matting = matting;
        _superResolution = superResolution;
        _segmentation = segmentation;
        _vision = vision;
    }
}
