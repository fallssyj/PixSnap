namespace PixSnap.Models;

/// <summary>AI 模型下载镜像。</summary>
public enum ModelDownloadMirror
{
    /// <summary>国内镜像 hf-mirror.com。</summary>
    HfMirror = 0,

    /// <summary>Hugging Face 官方 huggingface.co。</summary>
    HuggingFace = 1
}
