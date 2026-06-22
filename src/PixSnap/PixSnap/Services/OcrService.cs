using PixSnap.Models;
using PixSnap.Services.OcrLayout;
using RapidOCRLib;
using RapidOCRLib.Models;
using Serilog;
using SkiaSharp;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;
using RapidOcrRawResult = RapidOCRLib.Models.OcrResult;
using WpfPoint = System.Windows.Point;

namespace PixSnap.Services;

/// <summary>
/// 离线 OCR：基于 PaddleOCR（PP-OCRv5）ONNX 模型，通过 RapidOCR 推理。
/// 模型目录：<程序目录>/onnx/ocr/
/// </summary>
public static class OcrService
{
    private const int DefaultPadding = 24;

    /// <summary>检测阶段最长边上限（超过则等比缩小后再检测，兼顾速度与准确率）。</summary>
    private const int MobileDetectMaxSideLen = 1920;
    private const int ServerDetectMaxSideLen = 2560;

    /// <summary>最长边低于此值时，OCR 前 2× 放大以提升小字号 UI 文字准确率。</summary>
    private const int UpscaleWhenLongestSideBelow = 1280;

    private const int OcrUpscaleFactor = 2;

    /// <summary>截图文字几乎均为正向，关闭方向分类可提速并避免误旋转导致乱码。</summary>
    private const bool UseAngleClassifier = false;

    /// <summary>PaddleOCR PP-OCRv5 默认 det_db_box_thresh。</summary>
    private const float DetBoxScoreThresh = 0.60f;

    /// <summary>PaddleOCR 默认 det_db_thresh。</summary>
    private const float DetBoxThresh = 0.30f;

    /// <summary>PP-OCRv5 配置 unclip_ratio。</summary>
    private const float DetUnClipRatio = 1.50f;

    /// <summary>PaddleOCR 默认 drop_score：识别置信度低于此值的结果丢弃。</summary>
    private const float RecDropScore = 0.50f;

    /// <summary>单字结果使用更严阈值，减少 UI 图标误识别。</summary>
    private const float SingleCharRecDropScore = 0.68f;

    /// <summary>小尺寸近方形单字块（典型 toolbar 图标）的识别阈值。</summary>
    private const float IconLikeRecDropScore = 0.82f;

    /// <summary>原图像素坐标下，视为 UI 图标的最大边长。</summary>
    private const double MaxIconLikeBoxSide = 52.0;

    /// <summary>宽高比超过此值且高度像 UI 行高时，视为「图标 + 文字」合并框。</summary>
    private const double IconPlusTextMinAspectRatio = 2.40;

    /// <summary>UI 行最大高度（像素，原图坐标）。</summary>
    private const double IconPlusTextMaxRowHeight = 96.0;

    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "onnx", "ocr");
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static readonly SemaphoreSlim InferenceLock = new(1, 1);
    private static OcrLite? _engine;
    private static string? _engineProfile;

    public sealed record OcrResult(
        string FullText,
        string EngineName,
        IReadOnlyList<OcrTextRegion> Regions,
        int ImageWidth,
        int ImageHeight);

    public sealed class OcrNotAvailableException(string message) : Exception(message);

    public static bool IsAvailable => AiModelCatalog.IsOcrTierReady(OcrSettings.Tier);

    private static OcrModelTier CurrentTier => OcrSettings.Tier;

    private static string DetPath => CurrentTier switch
    {
        OcrModelTier.Server => Path.Combine(ModelDir, "ch_PP-OCRv5_server_det.onnx"),
        _ => Path.Combine(ModelDir, "ch_PP-OCRv5_mobile_det.onnx")
    };

    private static string RecPath => CurrentTier switch
    {
        OcrModelTier.Server => Path.Combine(ModelDir, "ch_PP-OCRv5_server_rec_infer.onnx"),
        _ => Path.Combine(ModelDir, "ch_PP-OCRv5_mobile_rec_infer.onnx")
    };

    private static string ClsPath => Path.Combine(ModelDir, "ch_ppocr_mobile_v2.0_cls_infer.onnx");
    private static string DictPath => Path.Combine(ModelDir, "ppocrv5_dict.txt");

    public static async Task<OcrResult> RecognizeAsync(
        BitmapSource image,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report((0.05, "正在加载 PaddleOCR 模型..."));

        if (image.PixelWidth < 8 || image.PixelHeight < 8)
            throw new InvalidOperationException("图片尺寸过小，无法识别文字。");

        // 在首次 await 前于调用线程完成快照，避免非冻结位图的跨线程访问
        var snapshot = ImageIOService.CreateFrozenSnapshot(image);
        int origW = snapshot.PixelWidth;
        int origH = snapshot.PixelHeight;

        var engine = await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report((0.20, "正在识别文字..."));

        var (pixels, ocrW, ocrH, coordScale) = PrepareOcrPixels(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        int maxSideLen = ComputeDetectMaxSideLen(ocrW, ocrH);
        await InferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        RapidOcrRawResult? raw = null;
        List<OcrTextRegion> regions = [];
        string resultFullText = string.Empty;
        int filteredCount;
        int refinedCount;
        try
        {
            raw = await engine.DetectAsyncFromBgra32(
                pixels,
                ocrW,
                ocrH,
                padding: DefaultPadding,
                maxSideLen: maxSideLen,
                boxScoreThresh: DetBoxScoreThresh,
                boxThresh: DetBoxThresh,
                unClipRatio: DetUnClipRatio,
                doAngle: UseAngleClassifier,
                mostAngle: false).ConfigureAwait(false);

            regions = new List<OcrTextRegion>();
            filteredCount = 0;
            refinedCount = 0;

            foreach (var block in raw.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text))
                    continue;

                var points = MapBoxPoints(block.BoxPoints, DefaultPadding, ocrW, ocrH);
                if (coordScale != 1.0)
                {
                    points = points
                        .Select(p => new WpfPoint(
                            Math.Clamp(p.X / coordScale, 0, origW - 1),
                            Math.Clamp(p.Y / coordScale, 0, origH - 1)))
                        .ToArray();
                }

                var bounds = GetAxisAlignedBounds(points);
                if (bounds.Width < 1 || bounds.Height < 1)
                    continue;

                string text = block.Text.Trim();
                float recScore = GetRecConfidence(block);

                if (IsIconPlusTextMergedRow(bounds))
                {
                    var refined = await TryRefineIconPlusTextAsync(
                        engine, snapshot, bounds, text, recScore, cancellationToken).ConfigureAwait(false);
                    if (refined.HasValue)
                    {
                        text = refined.Value.Text;
                        recScore = refined.Value.Score;
                        bounds = refined.Value.Bounds;
                        points = refined.Value.Points;
                        refinedCount++;
                    }
                }

                if (!ShouldKeepBlock(text, bounds, block.BoxScore, recScore))
                {
                    filteredCount++;
                    continue;
                }

                regions.Add(new OcrTextRegion
                {
                    Text = text,
                    PixelBounds = bounds,
                    BoxPoints = points,
                    Confidence = recScore
                });
            }

            regions = OcrLayoutMerger.MergeMultiParagraph(regions).ToList();
            resultFullText = OcrLayoutMerger.ToFullText(regions);
        }
        finally
        {
            raw?.BoxImg?.Dispose();
            InferenceLock.Release();
        }

        progress?.Report((1.0, string.Format("识别完成，共 {0} 处", regions.Count)));
        Log.Information(
            "PaddleOCR 完成: {Count} 块, 过滤 {Filtered}, 图标行修正 {Refined}, {W}×{H}, profile={Profile}, scale={Scale}, maxSideLen={MaxSide}",
            regions.Count, filteredCount, refinedCount, origW, origH, _engineProfile, coordScale, maxSideLen);

        return new OcrResult(
            resultFullText,
            EngineDisplayName,
            regions,
            origW,
            origH);
    }

    private static string EngineDisplayName => CurrentTier switch
    {
        OcrModelTier.Server => "PaddleOCR PP-OCRv5 Server（离线）",
        _ => "PaddleOCR PP-OCRv5 Mobile（离线）"
    };

    /// <summary>后台预热 OCR 引擎，减少首次识别等待。</summary>
    public static async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return;

        try
        {
            await EnsureEngineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OCR 预热失败");
        }
    }

    private static async Task<OcrLite> EnsureEngineAsync(CancellationToken cancellationToken)
    {
        var profile = $"{CurrentTier}|{DetPath}|{RecPath}";
        if (_engine is not null && _engineProfile == profile)
            return _engine;

        await InitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null && _engineProfile == profile)
                return _engine;

            if (!IsAvailable)
            {
                throw new OcrNotAvailableException(
                    string.Format("未找到当前规格（{0}）所需的 OCR 模型。\n请在「设置 → AI → 模型管理」中下载。", CurrentTier));
            }

            var previous = _engine;
            _engine = new OcrLite
            {
                DetPath = DetPath,
                ClsPath = ClsPath,
                RecPath = RecPath,
                KeyDicPath = DictPath,
                SkipVisualization = true,
                ThreadNum = ResolveThreadCount()
            };
            await _engine.InitModels().ConfigureAwait(false);
            previous?.Dispose();
            _engineProfile = profile;
            Log.Information("PaddleOCR 引擎已加载: {Tier}", CurrentTier);
            return _engine;
        }
        finally
        {
            InitLock.Release();
        }
    }

    private static (byte[] Pixels, int Width, int Height, double Scale) PrepareOcrPixels(BitmapSource snapshot)
    {
        int width = snapshot.PixelWidth;
        int height = snapshot.PixelHeight;
        int longest = Math.Max(width, height);

        if (longest >= UpscaleWhenLongestSideBelow)
            return (CopyBgra32Pixels(snapshot), width, height, 1.0);

        using var sk = SkiaInteropHelper.BitmapSourceToSKBitmap(snapshot);
        using var upscaled = SkiaInteropHelper.Upscale(sk, OcrUpscaleFactor);
        double scale = upscaled.Width / (double)width;
        return (SkiaInteropHelper.CopyPixels(upscaled), upscaled.Width, upscaled.Height, scale);
    }

    private static int ResolveThreadCount() =>
        AiGpuSettings.ShouldUseDirectMl ? 1 : Math.Max(1, Environment.ProcessorCount / 2);

    private static int ComputeDetectMaxSideLen(int width, int height)
    {
        int cap = CurrentTier == OcrModelTier.Server ? ServerDetectMaxSideLen : MobileDetectMaxSideLen;
        int longest = Math.Max(width, height);
        return longest > cap ? cap : 0;
    }

    private static byte[] CopyBgra32Pixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[height * stride];
        bgra.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        return pixels;
    }

    private static WpfPoint[] MapBoxPoints(List<DrawingPoint> points, int padding, int width, int height)
    {
        return points
            .Select(p => new WpfPoint(
                Math.Clamp(p.X - padding, 0, Math.Max(0, width - 1)),
                Math.Clamp(p.Y - padding, 0, Math.Max(0, height - 1))))
            .ToArray();
    }

    private static float GetRecConfidence(TextBlock block)
    {
        if (block.CharScores is not { Count: > 0 })
            return 0f;

        // 与 PaddleOCR 一致：取各字符置信度的最小值作为整行得分
        return block.CharScores.Min();
    }

    private static bool ShouldKeepBlock(string text, Rect bounds, float boxScore, float recScore)
    {
        if (boxScore < DetBoxScoreThresh)
            return false;

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        if (recScore < RecDropScore)
            return false;

        if (trimmed.Length == 1)
        {
            if (recScore < SingleCharRecDropScore)
                return false;

            if (IsLikelyIconBox(bounds) && recScore < IconLikeRecDropScore)
                return false;
        }

        return true;
    }

    private static bool IsLikelyIconBox(Rect bounds)
    {
        double w = bounds.Width;
        double h = bounds.Height;
        if (w <= 0 || h <= 0)
            return false;

        double maxSide = Math.Max(w, h);
        if (maxSide > MaxIconLikeBoxSide)
            return false;

        double ratio = w / h;
        return ratio is >= 0.70 and <= 1.40;
    }

    private static bool IsIconPlusTextMergedRow(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        if (bounds.Height > IconPlusTextMaxRowHeight)
            return false;

        return bounds.Width / bounds.Height >= IconPlusTextMinAspectRatio;
    }

    private static async Task<(string Text, float Score, Rect Bounds, WpfPoint[] Points)?> TryRefineIconPlusTextAsync(
        OcrLite engine,
        BitmapSource snapshot,
        Rect bounds,
        string originalText,
        float originalScore,
        CancellationToken cancellationToken)
    {
        int iconSkip = ComputeIconSkipPixels(bounds);
        int textX = (int)Math.Round(bounds.X) + iconSkip;
        int textY = (int)Math.Round(bounds.Y);
        int textW = (int)Math.Round(bounds.Width) - iconSkip;
        int textH = (int)Math.Round(bounds.Height);

        if (textW < 12 || textH < 6)
            return null;

        var cropRect = ClampRect(new Int32Rect(textX, textY, textW, textH), snapshot.PixelWidth, snapshot.PixelHeight);
        if (cropRect.Width < 12 || cropRect.Height < 6)
            return null;

        var cropPixels = CropBgra32(snapshot, cropRect);
        cancellationToken.ThrowIfCancellationRequested();

        var line = await engine.RecognizeLineFromBgra32Async(cropPixels, cropRect.Width, cropRect.Height)
            .ConfigureAwait(false);
        string refinedText = line.Text.Trim();
        float refinedScore = line.CharScores is { Count: > 0 } ? line.CharScores.Min() : 0f;

        if (!ShouldPreferRefinedText(originalText, originalScore, refinedText, refinedScore))
            return null;

        var textBounds = new Rect(cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
        var textPoints = RectToPoints(textBounds);
        return (refinedText, refinedScore, textBounds, textPoints);
    }

    private static int ComputeIconSkipPixels(Rect bounds)
    {
        int byHeight = (int)Math.Ceiling(bounds.Height * 1.05 + 4);
        int maxSkip = (int)Math.Floor(bounds.Width * 0.55);
        return Math.Clamp(byHeight, 8, Math.Max(8, maxSkip));
    }

    private static bool ShouldPreferRefinedText(string original, float originalScore, string refined, float refinedScore)
    {
        if (string.IsNullOrWhiteSpace(refined))
            return false;

        if (refinedScore < RecDropScore)
            return false;

        if (refinedScore >= originalScore + 0.03f)
            return true;

        if (refined.Length > original.Length && refinedScore >= originalScore - 0.05f)
            return true;

        return original.Length > 0
               && refined.StartsWith(original, StringComparison.OrdinalIgnoreCase)
               && refined.Length > original.Length;
    }

    private static Int32Rect ClampRect(Int32Rect rect, int imageWidth, int imageHeight)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, imageWidth - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, imageHeight - 1));
        int maxW = Math.Max(0, imageWidth - x);
        int maxH = Math.Max(0, imageHeight - y);
        return new Int32Rect(x, y, Math.Clamp(rect.Width, 0, maxW), Math.Clamp(rect.Height, 0, maxH));
    }

    private static byte[] CropBgra32(BitmapSource source, Int32Rect rect)
    {
        var cropped = new CroppedBitmap(source, rect);
        return CopyBgra32Pixels(cropped);
    }

    private static WpfPoint[] RectToPoints(Rect rect)
    {
        return
        [
            new WpfPoint(rect.Left, rect.Top),
            new WpfPoint(rect.Right, rect.Top),
            new WpfPoint(rect.Right, rect.Bottom),
            new WpfPoint(rect.Left, rect.Bottom)
        ];
    }

    private static Rect GetAxisAlignedBounds(IReadOnlyList<WpfPoint> points)
    {
        double minX = points.Min(p => p.X);
        double maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    /// <summary>应用退出时释放 OCR 引擎，避免 ONNX Session 泄漏。</summary>
    public static void Shutdown()
    {
        bool acquired = false;
        try
        {
            acquired = InferenceLock.Wait(TimeSpan.FromSeconds(3));
            if (!acquired)
                Log.Warning("等待 OCR 推理结束超时，仍将尝试释放引擎");

            _engine?.Dispose();
            _engine = null;
            _engineProfile = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放 OCR 引擎时出错");
        }
        finally
        {
            if (acquired)
                InferenceLock.Release();
        }
    }
}
