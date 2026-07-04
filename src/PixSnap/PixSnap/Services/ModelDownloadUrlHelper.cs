using PixSnap.Models;

namespace PixSnap.Services;

/// <summary>按用户设置将 Hugging Face 模型地址切换为国内镜像或官方源。</summary>
public static class ModelDownloadUrlHelper
{
    private const string HuggingFacePrefix = "https://huggingface.co/";
    private const string HfMirrorPrefix = "https://hf-mirror.com/";

    public static string Resolve(string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            return downloadUrl ?? string.Empty;

        return SettingsService.ReadModelDownloadMirror() switch
        {
            ModelDownloadMirror.HuggingFace => ToOfficial(downloadUrl),
            _ => ToMirror(downloadUrl)
        };
    }

    private static string ToMirror(string url)
    {
        if (url.StartsWith(HuggingFacePrefix, StringComparison.OrdinalIgnoreCase))
            return HfMirrorPrefix + url[HuggingFacePrefix.Length..];

        return url;
    }

    private static string ToOfficial(string url)
    {
        if (url.StartsWith(HfMirrorPrefix, StringComparison.OrdinalIgnoreCase))
            return HuggingFacePrefix + url[HfMirrorPrefix.Length..];

        return url;
    }
}
