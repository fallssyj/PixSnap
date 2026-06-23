namespace PixSnap.Models;

/// <summary>模型在应用内的接入状态。</summary>
public enum AiModelIntegrationStatus
{
    /// <summary>已接入，设置中选择后即可使用。</summary>
    Active = 0,

    /// <summary>已接入但非必需（如 OCR 方向分类已关闭）。</summary>
    Optional = 1,

    /// <summary>可下载，功能尚未开放。</summary>
    Planned = 2
}

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

    /// <summary>接入状态，用于模型管理与设置页展示。</summary>
    public AiModelIntegrationStatus IntegrationStatus { get; init; } = AiModelIntegrationStatus.Active;
}
