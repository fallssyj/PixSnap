using SkiaSharp;
using System.Windows;
using System.Windows.Media;
using MediaPoint = System.Windows.Point;

namespace PixSnap.Services;

/// <summary>画笔路径采样、简化与平滑绘制。</summary>
internal static class AnnotationPenHelper
{
    private const double MinSampleDistance = 2.0;
    private const double SimplifyEpsilon = 1.5;

    public static bool ShouldAddPoint(MediaPoint last, MediaPoint next)
    {
        var dx = next.X - last.X;
        var dy = next.Y - last.Y;
        return dx * dx + dy * dy >= MinSampleDistance * MinSampleDistance;
    }

    public static List<MediaPoint> Simplify(IReadOnlyList<MediaPoint> points)
    {
        if (points.Count <= 2)
            return [.. points];

        return DouglasPeucker(points, 0, points.Count - 1, SimplifyEpsilon);
    }

    public static PathGeometry BuildSmoothPath(IReadOnlyList<MediaPoint> points, double offsetX, double offsetY, double scale)
    {
        var geometry = new PathGeometry();
        if (points.Count == 0)
            return geometry;

        var figure = new PathFigure
        {
            StartPoint = ToCanvas(points[0], offsetX, offsetY, scale),
            IsClosed = false,
            IsFilled = false
        };

        if (points.Count == 1)
        {
            geometry.Figures.Add(figure);
            return geometry;
        }

        if (points.Count == 2)
        {
            figure.Segments.Add(new LineSegment(ToCanvas(points[1], offsetX, offsetY, scale), true));
            geometry.Figures.Add(figure);
            return geometry;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Count - 1)];

            var cp1 = ToCanvas(
                new MediaPoint(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0),
                offsetX, offsetY, scale);
            var cp2 = ToCanvas(
                new MediaPoint(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0),
                offsetX, offsetY, scale);
            var end = ToCanvas(p2, offsetX, offsetY, scale);
            figure.Segments.Add(new BezierSegment(cp1, cp2, end, true));
        }

        geometry.Figures.Add(figure);
        return geometry;
    }

    public static SKPath BuildSmoothSkiaPath(IReadOnlyList<MediaPoint> points, float offsetX, float offsetY)
    {
        var path = new SKPath();
        if (points.Count == 0)
            return path;

        path.MoveTo((float)points[0].X + offsetX, (float)points[0].Y + offsetY);
        if (points.Count == 1)
            return path;

        if (points.Count == 2)
        {
            path.LineTo((float)points[1].X + offsetX, (float)points[1].Y + offsetY);
            return path;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Count - 1)];

            var cp1x = (float)(p1.X + (p2.X - p0.X) / 6.0) + offsetX;
            var cp1y = (float)(p1.Y + (p2.Y - p0.Y) / 6.0) + offsetY;
            var cp2x = (float)(p2.X - (p3.X - p1.X) / 6.0) + offsetX;
            var cp2y = (float)(p2.Y - (p3.Y - p1.Y) / 6.0) + offsetY;
            var endX = (float)p2.X + offsetX;
            var endY = (float)p2.Y + offsetY;
            path.CubicTo(cp1x, cp1y, cp2x, cp2y, endX, endY);
        }

        return path;
    }

    private static MediaPoint ToCanvas(MediaPoint p, double offsetX, double offsetY, double scale) =>
        new(p.X * scale + offsetX, p.Y * scale + offsetY);

    private static List<MediaPoint> DouglasPeucker(IReadOnlyList<MediaPoint> points, int start, int end, double epsilon)
    {
        if (end <= start + 1)
            return [points[start], points[end]];

        double maxDist = 0;
        int index = start;
        var a = points[start];
        var b = points[end];

        for (int i = start + 1; i < end; i++)
        {
            var dist = PerpendicularDistance(points[i], a, b);
            if (dist > maxDist)
            {
                maxDist = dist;
                index = i;
            }
        }

        if (maxDist > epsilon)
        {
            var left = DouglasPeucker(points, start, index, epsilon);
            var right = DouglasPeucker(points, index, end, epsilon);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }

        return [a, b];
    }

    private static double PerpendicularDistance(MediaPoint p, MediaPoint a, MediaPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0)
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var projX = a.X + t * dx;
        var projY = a.Y + t * dy;
        var ex = p.X - projX;
        var ey = p.Y - projY;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
