namespace PixSnap.Services;

using PixSnap.Models;

/// <summary>OCR 模型规格（Mobile / Server）运行时偏好。</summary>
public static class OcrSettings
{
    private static OcrModelTier _tier = OcrModelTier.Mobile;

    public static OcrModelTier Tier => _tier;

    public static void LoadFromSettings() => _tier = SettingsService.ReadOcrModelTier();

    public static void Apply(OcrModelTier tier)
    {
        if (_tier == tier)
            return;

        _tier = tier;
        OcrService.Shutdown();
    }
}
