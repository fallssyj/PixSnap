using PixSnap.Models;

using System.IO;



namespace PixSnap.Services;



/// <summary>应用内所有 AI ONNX 模型清单。</summary>

public static class AiModelCatalog

{

    public static IReadOnlyList<AiModelDescriptor> All { get; } =

    [

        // ── 文字识别 · Mobile ──

        new()

        {

            Id = "ocr-mobile-det",

            DisplayName = "PP-OCRv5 Mobile · 检测",

            UsageHint = "定位截图中的文字区域",

            Category = "文字识别",

            SortOrder = 10,

            RelativePath = "onnx/ocr/ch_PP-OCRv5_mobile_det.onnx",

            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det_onnx/resolve/main/inference.onnx",

            ApproximateSizeBytes = 4_800_000

        },

        new()

        {

            Id = "ocr-mobile-rec",

            DisplayName = "PP-OCRv5 Mobile · 识别",

            UsageHint = "识别检测框内的文字内容",

            Category = "文字识别",

            SortOrder = 11,

            RelativePath = "onnx/ocr/ch_PP-OCRv5_mobile_rec_infer.onnx",

            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_rec_onnx/resolve/main/inference.onnx",

            ApproximateSizeBytes = 16_000_000

        },

        // ── 文字识别 · Server ──

        new()

        {

            Id = "ocr-server-det",

            DisplayName = "PP-OCRv5 Server · 检测",

            UsageHint = "高精度检测，适合小字与复杂版面",

            Category = "文字识别",

            SortOrder = 20,

            RelativePath = "onnx/ocr/ch_PP-OCRv5_server_det.onnx",

            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv5_server_det_onnx/resolve/main/inference.onnx",

            ApproximateSizeBytes = 88_000_000

        },

        new()

        {

            Id = "ocr-server-rec",

            DisplayName = "PP-OCRv5 Server · 识别",

            UsageHint = "高精度识别，设置中选 Server 时使用",

            Category = "文字识别",

            SortOrder = 21,

            RelativePath = "onnx/ocr/ch_PP-OCRv5_server_rec_infer.onnx",

            DownloadUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv5_server_rec_onnx/resolve/main/inference.onnx",

            ApproximateSizeBytes = 84_000_000

        },

        // ── 文字识别 · 共享 ──

        new()

        {

            Id = "ocr-cls",

            DisplayName = "方向分类 CLS",

            UsageHint = "OCR 共用；当前未启用方向分类，可不下载",

            Category = "文字识别",

            SortOrder = 30,

            RelativePath = "onnx/ocr/ch_ppocr_mobile_v2.0_cls_infer.onnx",

            DownloadUrl = "https://huggingface.co/Kreuzberg/paddleocr-onnx-models/resolve/main/ch_ppocr_mobile_v2.0_cls_infer.onnx",

            ApproximateSizeBytes = 2_100_000

        },

        new()

        {

            Id = "ocr-dict",

            DisplayName = "字符字典",

            UsageHint = "OCR 共用，随程序内置",

            Category = "文字识别",

            SortOrder = 31,

            RelativePath = "onnx/ocr/ppocrv5_dict.txt",

            DownloadUrl = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/release/3.0/ppocr/utils/dict/ppocrv5_dict.txt",

            ApproximateSizeBytes = 74_000,

            IsBundled = true

        },

        // ── 图像增强 · 抠图 ──

        new()

        {

            Id = "rmbg",

            DisplayName = "背景去除 · RMBG-1.4",

            UsageHint = "默认抠图模型，速度与体积均衡",

            Category = "图像增强",

            SortOrder = 10,

            RelativePath = "onnx/rmbg-1.4.onnx",

            DownloadUrl = "https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx",

            ApproximateSizeBytes = 176_000_000

        },

        new()

        {

            Id = "birefnet",

            DisplayName = "背景去除 · BiRefNet FP16",

            UsageHint = "高分辨率抠图，适合复杂前景",

            Category = "图像增强",

            SortOrder = 12,

            RelativePath = "onnx/birefnet-fp16.onnx",

            DownloadUrl = "https://huggingface.co/onnx-community/BiRefNet-ONNX/resolve/main/onnx/model_fp16.onnx",

            ApproximateSizeBytes = 490_000_000

        },

        // ── 图像增强 · 超分 ──

        new()

        {

            Id = "realesrgan",

            DisplayName = "超分辨率 4×",

            UsageHint = "Real-ESRGAN x4plus；设置中选 2× 时自动半分辨率推理，速度与 4× 相当",

            Category = "图像增强",

            SortOrder = 20,

            RelativePath = "onnx/realesrgan-x4plus.onnx",

            DownloadUrl = "https://huggingface.co/qualcomm/Real-ESRGAN-x4plus/resolve/01179a4da7bf5ac91faca650e6afbf282ac93933/Real-ESRGAN-x4plus.onnx",

            ApproximateSizeBytes = 67_000_000

        },

        // ── 图像增强 · AI 擦除 ──

        new()

        {

            Id = "lama",

            DisplayName = "AI 擦除",

            UsageHint = "LaMa FP32，涂抹后智能修复",

            Category = "图像增强",

            SortOrder = 30,

            RelativePath = "onnx/lama_fp32.onnx",

            DownloadUrl = "https://huggingface.co/Carve/LaMa-ONNX/resolve/main/lama_fp32.onnx",

            ApproximateSizeBytes = 210_000_000

        },

    ];



    public static IReadOnlyList<string> Categories { get; } =

        ["文字识别", "图像增强"];



    public static IEnumerable<AiModelDescriptor> GetByCategory(string category) =>

        All.Where(m => m.Category == category).OrderBy(m => m.SortOrder);



    public static string GetAbsolutePath(AiModelDescriptor model) =>

        Path.Combine(AppContext.BaseDirectory, model.RelativePath);



    public static bool IsDownloaded(AiModelDescriptor model)

    {

        var path = GetAbsolutePath(model);

        if (!File.Exists(path))

            return false;



        if (model.IsBundled)

            return true;



        return new FileInfo(path).Length > 1024;

    }



    public static long? GetFileSizeBytes(AiModelDescriptor model)

    {

        var path = GetAbsolutePath(model);

        return File.Exists(path) ? new FileInfo(path).Length : model.ApproximateSizeBytes;

    }



    public static IReadOnlyList<AiModelDescriptor> GetOcrModelsForTier(OcrModelTier tier) =>

        tier switch

        {

            OcrModelTier.Server =>

            [

                Require("ocr-server-det"),

                Require("ocr-server-rec"),

                Require("ocr-cls"),

                Require("ocr-dict")

            ],

            _ =>

            [

                Require("ocr-mobile-det"),

                Require("ocr-mobile-rec"),

                Require("ocr-cls"),

                Require("ocr-dict")

            ]

        };



    public static bool IsOcrTierReady(OcrModelTier tier) =>
        GetOcrModelsForTier(tier)
            .Where(m => ResolveIntegrationStatus(m) != AiModelIntegrationStatus.Optional)
            .All(IsDownloaded);



    public static IReadOnlyList<AiModelDescriptor> GetMattingModels(MattingModel model) =>

        model switch

        {

            MattingModel.BiRefNet => [Require("birefnet")],

            _ => [Require("rmbg")]

        };



    public static bool IsMattingReady(MattingModel model) =>

        GetMattingModels(model).All(IsDownloaded);



    public static string GetMattingModelPath(MattingModel model) =>

        GetAbsolutePath(GetMattingModels(model)[0]);



    public static IReadOnlyList<AiModelDescriptor> GetSuperResolutionModels(SuperResolutionModel model) =>

        model switch

        {

            SuperResolutionModel.X2 => [Require("realesrgan")],

            _ => [Require("realesrgan")]

        };



    public static bool IsSuperResolutionReady(SuperResolutionModel model) =>

        GetSuperResolutionModels(model).All(IsDownloaded);



    public static string GetSuperResolutionModelPath(SuperResolutionModel model) =>

        GetAbsolutePath(GetSuperResolutionModels(model)[0]);



    public static int GetSuperResolutionScale(SuperResolutionModel model) =>

        model == SuperResolutionModel.X2 ? 2 : 4;



    public static string FormatSize(long? bytes)

    {

        if (bytes is null or <= 0)

            return "—";



        double value = bytes.Value;

        string[] units = ["B", "KB", "MB", "GB"];

        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)

        {

            value /= 1024;

            unit++;

        }



        return $"{value:0.#} {units[unit]}";

    }



    public static AiModelDescriptor? FindByAbsolutePath(string? path)

    {

        if (string.IsNullOrWhiteSpace(path))

            return null;



        string fullPath;

        try

        {

            fullPath = Path.GetFullPath(path);

        }

        catch

        {

            return null;

        }



        return All.FirstOrDefault(m =>

            string.Equals(GetAbsolutePath(m), fullPath, StringComparison.OrdinalIgnoreCase));

    }



    public static AiModelDescriptor? FindByFileName(string? fileName)

    {

        if (string.IsNullOrWhiteSpace(fileName))

            return null;



        return All.FirstOrDefault(m =>

            string.Equals(Path.GetFileName(m.RelativePath), fileName, StringComparison.OrdinalIgnoreCase));

    }



    public static AiModelDescriptor? FindById(string id) =>

        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    public static AiModelDescriptor GetRequired(string id) => Require(id);

    public static IReadOnlyList<AiModelDescriptor> GetModelsForFeature(string featureName) =>

        featureName switch

        {

            "去除背景" => GetMattingModels(AiFeatureSettings.Matting),

            "超分辨率" => GetSuperResolutionModels(AiFeatureSettings.SuperResolution),

            "AI 擦除" => [Require("lama")],

            _ => FindById(FeatureModelIds.GetValueOrDefault(featureName, string.Empty)) is { } single

                ? [single]

                : []

        };



    private static readonly Dictionary<string, string> FeatureModelIds =

        new(StringComparer.Ordinal)

        {

            ["去除背景"] = "rmbg",

            ["超分辨率"] = "realesrgan",

            ["AI 擦除"] = "lama"

        };



    private static readonly Dictionary<string, AiModelIntegrationStatus> IntegrationOverrides =
        new(StringComparer.Ordinal)
        {
            ["ocr-cls"] = AiModelIntegrationStatus.Optional
        };

    public static AiModelIntegrationStatus ResolveIntegrationStatus(AiModelDescriptor model) =>
        IntegrationOverrides.GetValueOrDefault(model.Id, model.IntegrationStatus);

    public static string GetIntegrationLabel(AiModelDescriptor model) =>
        ResolveIntegrationStatus(model) switch
        {
            AiModelIntegrationStatus.Optional => "可选",
            AiModelIntegrationStatus.Planned => "即将支持",
            _ => "已接入"
        };

    public static bool IsFeatureSelectable(AiModelDescriptor model) =>
        ResolveIntegrationStatus(model) != AiModelIntegrationStatus.Planned;

    private static AiModelDescriptor Require(string id) =>

        FindById(id) ?? throw new InvalidOperationException($"未在模型目录中注册：{id}");

}


