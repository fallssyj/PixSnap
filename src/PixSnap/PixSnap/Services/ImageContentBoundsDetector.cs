using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>从点击位置出发，检测连通的前景区域外接矩形（适用于透明底或纯色底图片）。</summary>
internal static class ImageContentBoundsDetector
{
    private const byte AlphaForegroundThreshold = 24;
    private const int OpaqueColorDistanceThreshold = 36;
    private const int DefaultPadding = 4;

    public static bool TryDetectBounds(
        BitmapSource source,
        int seedX,
        int seedY,
        out int x,
        out int y,
        out int width,
        out int height,
        int padding = DefaultPadding)
    {
        x = y = width = height = 0;

        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return false;

        var formatted = EnsurePbgra32(source);
        int w = formatted.PixelWidth;
        int h = formatted.PixelHeight;
        seedX = Math.Clamp(seedX, 0, w - 1);
        seedY = Math.Clamp(seedY, 0, h - 1);

        var pixels = new byte[w * h * 4];
        formatted.CopyPixels(pixels, w * 4, 0);

        bool useAlpha = HasMeaningfulAlpha(pixels);
        var background = useAlpha ? default : EstimateBackgroundColor(pixels, w, h);

        int seedIndex = (seedY * w + seedX) * 4;
        if (!IsForeground(pixels, seedIndex, useAlpha, background))
            return false;

        var visited = new bool[w * h];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((seedX, seedY));
        visited[seedY * w + seedX] = true;

        int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;
        int count = 0;
        const int maxPixels = 12_000_000;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            count++;
            if (count > maxPixels)
                break;

            minX = Math.Min(minX, cx);
            maxX = Math.Max(maxX, cx);
            minY = Math.Min(minY, cy);
            maxY = Math.Max(maxY, cy);

            TryEnqueue(cx - 1, cy);
            TryEnqueue(cx + 1, cy);
            TryEnqueue(cx, cy - 1);
            TryEnqueue(cx, cy + 1);

            void TryEnqueue(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                    return;

                int flat = ny * w + nx;
                if (visited[flat])
                    return;

                int idx = flat * 4;
                if (!IsForeground(pixels, idx, useAlpha, background))
                    return;

                visited[flat] = true;
                queue.Enqueue((nx, ny));
            }
        }

        if (maxX <= minX || maxY <= minY)
            return false;

        x = Math.Clamp(minX - padding, 0, w - 1);
        y = Math.Clamp(minY - padding, 0, h - 1);
        int right = Math.Clamp(maxX + padding, 0, w - 1);
        int bottom = Math.Clamp(maxY + padding, 0, h - 1);
        width = Math.Max(1, right - x + 1);
        height = Math.Max(1, bottom - y + 1);
        return true;
    }

    /// <summary>扫描整张图片，返回所有前景像素的外接矩形（用于智能裁剪按钮）。</summary>
    public static bool TryDetectFullContentBounds(
        BitmapSource source,
        out int x,
        out int y,
        out int width,
        out int height,
        int padding = DefaultPadding)
    {
        x = y = width = height = 0;

        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return false;

        var formatted = EnsurePbgra32(source);
        int w = formatted.PixelWidth;
        int h = formatted.PixelHeight;

        var pixels = new byte[w * h * 4];
        formatted.CopyPixels(pixels, w * 4, 0);

        bool useAlpha = HasMeaningfulAlpha(pixels);
        var background = useAlpha ? default : EstimateBackgroundColor(pixels, w, h);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        bool found = false;

        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                int idx = (py * w + px) * 4;
                if (!IsForeground(pixels, idx, useAlpha, background))
                    continue;

                found = true;
                minX = Math.Min(minX, px);
                maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py);
                maxY = Math.Max(maxY, py);
            }
        }

        if (!found || maxX < minX || maxY < minY)
            return false;

        x = Math.Clamp(minX - padding, 0, w - 1);
        y = Math.Clamp(minY - padding, 0, h - 1);
        int right = Math.Clamp(maxX + padding, 0, w - 1);
        int bottom = Math.Clamp(maxY + padding, 0, h - 1);
        width = Math.Max(1, right - x + 1);
        height = Math.Max(1, bottom - y + 1);
        return true;
    }

    private static FormatConvertedBitmap EnsurePbgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Pbgra32)
            return new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        return new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
    }

    private static bool HasMeaningfulAlpha(byte[] pixels)
    {
        int step = Math.Max(4, pixels.Length / (4 * 4096));
        for (int i = 3; i < pixels.Length; i += step * 4)
        {
            if (pixels[i] < 250)
                return true;
        }

        return false;
    }

    private static (byte B, byte G, byte R) EstimateBackgroundColor(byte[] pixels, int w, int h)
    {
        long b = 0, g = 0, r = 0;

        void Accumulate(int px, int py)
        {
            int i = (py * w + px) * 4;
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
        }

        Accumulate(0, 0);
        Accumulate(w - 1, 0);
        Accumulate(0, h - 1);
        Accumulate(w - 1, h - 1);

        return ((byte)(b / 4), (byte)(g / 4), (byte)(r / 4));
    }

    private static bool IsForeground(byte[] pixels, int index, bool useAlpha, (byte B, byte G, byte R) background)
    {
        if (useAlpha)
            return pixels[index + 3] >= AlphaForegroundThreshold;

        int db = pixels[index] - background.B;
        int dg = pixels[index + 1] - background.G;
        int dr = pixels[index + 2] - background.R;
        return db * db + dg * dg + dr * dr > OpaqueColorDistanceThreshold * OpaqueColorDistanceThreshold;
    }
}
