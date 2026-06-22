namespace PixSnap.Models;

/// <summary>AI 功能使用的 ONNX 模型元数据。</summary>
public sealed class AiModelDescriptor
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Category { get; init; }

    /// <summary>一行说明，用于列表副标题。</summary>
    public string? UsageHint { get; init; }

    /// <summary>分组内排序权重。</summary>
    public int SortOrder { get; init; }

    /// <summary>相对于程序目录的路径，如 onnx/ocr/foo.onnx。</summary>
    public required string RelativePath { get; init; }

    public string? DownloadUrl { get; init; }

    /// <summary>约计大小（字节），用于 UI 展示。</summary>
    public long? ApproximateSizeBytes { get; init; }

    public bool IsBundled { get; init; }
}
