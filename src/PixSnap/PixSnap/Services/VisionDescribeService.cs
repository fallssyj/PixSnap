using Florence2;
using PixSnap.Models;
using Serilog;
using System.IO;
using System.Windows.Media.Imaging;

namespace PixSnap.Services;

/// <summary>截图视觉理解（Florence-2 / Moondream2）。</summary>
public static class VisionDescribeService
{
    private static readonly object FlorenceLock = new();
    private static Florence2Model? _florenceModel;

    public static bool IsAvailable =>
        AiModelCatalog.IsVisionReady(AiFeatureSettings.Vision);

    public sealed class VisionNotAvailableException(string message) : Exception(message);

    public static Task<string> DescribeAsync(
        BitmapSource image,
        string? prompt = null,
        IProgress<(double Value, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            var missing = AiModelCatalog.GetVisionModels(AiFeatureSettings.Vision)
                .Where(m => !AiModelCatalog.IsDownloaded(m))
                .Select(m => m.DisplayName)
                .ToList();
            string hint = missing.Count > 0
                ? $"缺少：{string.Join("、", missing)}"
                : "请在「模型管理」中下载视觉理解模型。";
            throw new VisionNotAvailableException(hint);
        }

        return AiFeatureSettings.Vision switch
        {
            VisionModel.Moondream2 => Task.FromException<string>(
                new NotSupportedException("Moondream2 截图描述推理将在后续版本开放。")),
            _ => DescribeWithFlorence2Async(image, prompt, progress, cancellationToken)
        };
    }

    private static async Task<string> DescribeWithFlorence2Async(
        BitmapSource image,
        string? prompt,
        IProgress<(double Value, string Text)>? progress,
        CancellationToken cancellationToken)
    {
        var frozen = ImageIOService.CreateFrozenSnapshot(image);
        progress?.Report((0.15, "正在加载 Florence-2 模型..."));
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = GetOrCreateFlorenceModel();
            progress?.Report((0.35, "正在分析截图内容..."));

            string tempPath = Path.Combine(Path.GetTempPath(), $"pixsnap-vision-{Guid.NewGuid():N}.png");
            try
            {
                ImageIOService.SavePngAsync(frozen, tempPath).GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();

                using var stream = File.OpenRead(tempPath);
                var task = string.IsNullOrWhiteSpace(prompt)
                    ? TaskTypes.CAPTION
                    : TaskTypes.DETAILED_CAPTION;
                var results = model.Run(task, [stream], null!, cancellationToken);
                string? text = results.FirstOrDefault()?.PureText?.Trim();
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException("未能生成有效描述。");

                progress?.Report((1.0, "AI 读图完成"));
                Log.Information("Florence-2 截图描述完成，长度 {Length}", text.Length);
                return text;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "清理临时截图失败: {Path}", tempPath);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Florence2Model GetOrCreateFlorenceModel()
    {
        lock (FlorenceLock)
        {
            if (_florenceModel is not null)
                return _florenceModel;

            var source = new CatalogFlorenceModelSource();
            if (!source.IsReady())
            {
                var missing = AiModelCatalog.GetVisionModels(VisionModel.Florence2)
                    .Where(m => !AiModelCatalog.IsDownloaded(m))
                    .Select(m => m.DisplayName);
                throw new VisionNotAvailableException($"缺少：{string.Join("、", missing)}");
            }

            Log.Information("正在初始化 Florence-2 推理引擎...");
            _florenceModel = new Florence2Model(source);
            return _florenceModel;
        }
    }

    private sealed class CatalogFlorenceModelSource : IModelSource
    {
        public bool IsReady() =>
            Enum.GetValues<IModelSource.Model>().All(m => TryGetModelPath(m, out _));

        public bool TryGetModelPath(IModelSource.Model model, out string modelPath)
        {
            modelPath = model switch
            {
                IModelSource.Model.VisionEncoder =>
                    AiModelCatalog.GetAbsolutePath(AiModelCatalog.GetRequired("florence2-vision")),
                IModelSource.Model.DecoderModelMerged =>
                    AiModelCatalog.GetAbsolutePath(AiModelCatalog.GetRequired("florence2-decoder")),
                IModelSource.Model.EncoderModel =>
                    AiModelCatalog.GetAbsolutePath(AiModelCatalog.GetRequired("florence2-encoder")),
                IModelSource.Model.EmbedTokens =>
                    AiModelCatalog.GetAbsolutePath(AiModelCatalog.GetRequired("florence2-embed")),
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, null)
            };

            return File.Exists(modelPath);
        }

        public byte[] GetModelBytes(IModelSource.Model model)
        {
            if (!TryGetModelPath(model, out var modelPath))
                throw new FileNotFoundException($"未找到 Florence-2 模型：{model}", modelPath);

            return File.ReadAllBytes(modelPath);
        }
    }
}
