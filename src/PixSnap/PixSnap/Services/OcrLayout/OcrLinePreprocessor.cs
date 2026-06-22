using System.Windows;

namespace PixSnap.Services.OcrLayout;

/// <summary>按行预处理：标准化 bbox、估计轻微旋转。参考 Umi-OCR line_preprocessing。</summary>
internal static class OcrLinePreprocessor
{
    private const double AngleThresholdDegrees = 3.0;

    public static void Preprocess(List<OcrLayoutBlock> blocks)
    {
        blocks.RemoveAll(b => string.IsNullOrWhiteSpace(b.Text));
        if (blocks.Count == 0)
            return;

        double rotationRad = EstimateRotation(blocks);
        ApplyNormalizedBboxes(blocks, rotationRad);
        blocks.Sort((a, b) => a.NormalizedBBox.Y0.CompareTo(b.NormalizedBBox.Y0));
    }

    private static double EstimateRotation(List<OcrLayoutBlock> blocks)
    {
        var angles = blocks.Select(b => CalculateAngle(b.Region.BoxPoints)).OrderBy(a => a).ToList();
        return angles[angles.Count / 2];
    }

    private static double CalculateAngle(Point[] box)
    {
        if (box.Length < 4)
            return 0;

        double width = Distance(box[0], box[1]);
        double height = Distance(box[1], box[2]);
        double angleRad = width < height
            ? Math.Atan2(box[2].Y - box[1].Y, box[2].X - box[1].X)
            : Math.Atan2(box[1].Y - box[0].Y, box[1].X - box[0].X);

        double threshold = AngleThresholdDegrees * Math.PI / 180.0;
        if (angleRad < -Math.PI / 2 + threshold)
            angleRad += Math.PI;
        else if (angleRad >= Math.PI / 2 + threshold)
            angleRad -= Math.PI;

        return angleRad;
    }

    private static void ApplyNormalizedBboxes(List<OcrLayoutBlock> blocks, double rotationRad)
    {
        double threshold = AngleThresholdDegrees * Math.PI / 180.0;
        if (Math.Abs(rotationRad) <= threshold)
        {
            foreach (var block in blocks)
                block.NormalizedBBox = ToAxisAlignedBBox(block.Region.BoxPoints);
            return;
        }

        double cosAngle = Math.Cos(-rotationRad);
        double sinAngle = Math.Sin(-rotationRad);
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        var bboxes = new List<(double X0, double Y0, double X1, double Y1)>();

        foreach (var block in blocks)
        {
            var rotated = block.Region.BoxPoints
                .Select(p => (
                    X: cosAngle * p.X - sinAngle * p.Y,
                    Y: sinAngle * p.X + cosAngle * p.Y))
                .ToList();
            double x0 = rotated.Min(p => p.X);
            double y0 = rotated.Min(p => p.Y);
            double x1 = rotated.Max(p => p.X);
            double y1 = rotated.Max(p => p.Y);
            bboxes.Add((x0, y0, x1, y1));
            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
        }

        if (minX < 0 || minY < 0)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                var (x0, y0, x1, y1) = bboxes[i];
                blocks[i].NormalizedBBox = (x0 - minX, y0 - minY, x1 - minX, y1 - minY);
            }
        }
        else
        {
            for (int i = 0; i < blocks.Count; i++)
                blocks[i].NormalizedBBox = bboxes[i];
        }
    }

    private static (double X0, double Y0, double X1, double Y1) ToAxisAlignedBBox(Point[] points)
    {
        double x0 = points.Min(p => p.X);
        double y0 = points.Min(p => p.Y);
        double x1 = points.Max(p => p.X);
        double y1 = points.Max(p => p.Y);
        return (x0, y0, x1, y1);
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
