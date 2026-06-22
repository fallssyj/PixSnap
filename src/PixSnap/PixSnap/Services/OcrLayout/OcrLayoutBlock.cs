using PixSnap.Models;

namespace PixSnap.Services.OcrLayout;

/// <summary>排版合并过程中的文块包装，算法参考 Umi-OCR tbpu/multi_para。</summary>
internal sealed class OcrLayoutBlock
{
    public required OcrTextRegion Region { get; init; }

    public (double X0, double Y0, double X1, double Y1) NormalizedBBox { get; set; }

    public string LineEnd { get; set; } = "\n";

    public string Text => Region.Text;
}
