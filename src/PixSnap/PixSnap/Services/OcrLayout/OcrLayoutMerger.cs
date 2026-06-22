using PixSnap.Models;

namespace PixSnap.Services.OcrLayout;

/// <summary>OCR 排版合并（多栏 + 自然段），行为对齐 Umi-OCR <c>multi_para</c>。</summary>
public static class OcrLayoutMerger
{
    public static IReadOnlyList<OcrTextRegion> MergeMultiParagraph(IReadOnlyList<OcrTextRegion> regions)
    {
        if (regions.Count == 0)
            return regions;

        if (regions.Count == 1)
        {
            var only = regions[0];
            return
            [
                new OcrTextRegion
                {
                    Text = only.Text,
                    PixelBounds = only.PixelBounds,
                    BoxPoints = only.BoxPoints,
                    Confidence = only.Confidence,
                    LineEnd = string.Empty
                }
            ];
        }

        var blocks = regions
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => new OcrLayoutBlock { Region = r })
            .ToList();

        if (blocks.Count <= 1)
            return ToRegions(blocks);

        OcrLinePreprocessor.Preprocess(blocks);
        var gapTree = new OcrGapTree(b => b.NormalizedBBox);
        var sorted = gapTree.Sort(blocks);

        foreach (var nodeBlocks in gapTree.GetNodesTextBlocks())
        {
            if (nodeBlocks.Count > 0)
                OcrParagraphParser.Run(nodeBlocks);
        }

        // 最后一个块末尾不需要额外分隔符
        if (sorted.Count > 0)
            sorted[^1].LineEnd = string.Empty;

        return ToRegions(sorted);
    }

    public static string ToFullText(IReadOnlyList<OcrTextRegion> regions) =>
        string.Concat(regions.Select(r => r.Text + r.LineEnd)).TrimEnd();

    private static IReadOnlyList<OcrTextRegion> ToRegions(List<OcrLayoutBlock> blocks) =>
        blocks.Select(b => new OcrTextRegion
        {
            Text = b.Region.Text,
            PixelBounds = b.Region.PixelBounds,
            BoxPoints = b.Region.BoxPoints,
            Confidence = b.Region.Confidence,
            LineEnd = b.LineEnd
        }).ToList();
}
