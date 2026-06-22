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

            UsageHint = "OCR 共用，判断文字朝向",

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

            DownloadUrl = "https://huggingface.co/AXERA-TECH/PPOCR_v5/resolve/main/ppocrv5_dict.txt",

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

            Id = "rmbg-2",

            DisplayName = "背景去除 · RMBG-2.0",

            UsageHint = "Bria 新一代抠图，边缘更精细",

            Category = "图像增强",

            SortOrder = 11,

            RelativePath = "onnx/rmbg-2.0.onnx",

            DownloadUrl = "https://huggingface.co/camenduru/RMBG-2.0/resolve/main/onnx/model_quantized.onnx",

            ApproximateSizeBytes = 366_000_000

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

            UsageHint = "Real-ESRGAN x4plus，高质量放大",

            Category = "图像增强",

            SortOrder = 20,

            RelativePath = "onnx/realesrgan-x4plus.onnx",

            DownloadUrl = "https://huggingface.co/qualcomm/Real-ESRGAN-x4plus/resolve/01179a4da7bf5ac91faca650e6afbf282ac93933/Real-ESRGAN-x4plus.onnx",

            ApproximateSizeBytes = 67_000_000

        },

        new()

        {

            Id = "realesrgan-x2",

            DisplayName = "超分辨率 2×",

            UsageHint = "Real-ESRGAN x2plus，更快、显存更省",

            Category = "图像增强",

            SortOrder = 21,

            RelativePath = "onnx/realesrgan-x2plus.onnx",

            DownloadUrl = "https://huggingface.co/tidus2102/Real-ESRGAN/resolve/main/Real-ESRGAN_x2plus.onnx",

            ApproximateSizeBytes = 64_000_000

        },

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

        // ── 图像分割 ──

        new()

        {

            Id = "mobilesam-encoder",

            DisplayName = "MobileSAM · 图像编码器",

            UsageHint = "点选分割第一步，提取图像特征",

            Category = "图像分割",

            SortOrder = 10,

            RelativePath = "onnx/segment/mobilesam_encoder.onnx",

            DownloadUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/mobile_sam_image_encoder.onnx",

            ApproximateSizeBytes = 28_000_000

        },

        new()

        {

            Id = "mobilesam-decoder",

            DisplayName = "MobileSAM · 掩码解码器",

            UsageHint = "点选分割第二步，根据点击生成分割蒙版",

            Category = "图像分割",

            SortOrder = 11,

            RelativePath = "onnx/segment/mobilesam_decoder.onnx",

            DownloadUrl = "https://huggingface.co/Acly/MobileSAM/resolve/main/sam_mask_decoder_single.onnx",

            ApproximateSizeBytes = 16_500_000

        },

        new()

        {

            Id = "fastsam",

            DisplayName = "FastSAM-S",

            UsageHint = "整图快速分割，适合全图物体检测",

            Category = "图像分割",

            SortOrder = 20,

            RelativePath = "onnx/segment/fastsam-s.onnx",

            DownloadUrl = "https://huggingface.co/qualcomm/FastSam-S/resolve/cc14010903ff67adb48a709774e224b31fe7e425/FastSam-S.onnx",

            ApproximateSizeBytes = 45_000_000

        },

        // ── 智能理解 · Florence-2 ──

        new()

        {

            Id = "florence2-vision",

            DisplayName = "Florence-2 · 视觉编码器",

            UsageHint = "截图描述 / 问答的视觉主干",

            Category = "智能理解",

            SortOrder = 10,

            RelativePath = "onnx/vision/florence2_vision_encoder.onnx",

            DownloadUrl = "https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/main/onnx/vision_encoder.onnx",

            ApproximateSizeBytes = 367_000_000

        },

        new()

        {

            Id = "florence2-decoder",

            DisplayName = "Florence-2 · 文本解码器",

            UsageHint = "生成截图描述与简短回答",

            Category = "智能理解",

            SortOrder = 11,

            RelativePath = "onnx/vision/florence2_decoder_merged.onnx",

            DownloadUrl = "https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/main/onnx/decoder_model_merged.onnx",

            ApproximateSizeBytes = 388_000_000

        },

        new()

        {

            Id = "florence2-encoder",

            DisplayName = "Florence-2 · 文本编码器",

            UsageHint = "截图描述任务的文字编码",

            Category = "智能理解",

            SortOrder = 12,

            RelativePath = "onnx/vision/florence2_encoder.onnx",

            DownloadUrl = "https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/main/onnx/encoder_model.onnx",

            ApproximateSizeBytes = 173_000_000

        },

        new()

        {

            Id = "florence2-embed",

            DisplayName = "Florence-2 · 词嵌入",

            UsageHint = "截图描述任务的 token 嵌入",

            Category = "智能理解",

            SortOrder = 13,

            RelativePath = "onnx/vision/florence2_embed_tokens.onnx",

            DownloadUrl = "https://huggingface.co/onnx-community/Florence-2-base-ft/resolve/main/onnx/embed_tokens.onnx",

            ApproximateSizeBytes = 157_000_000

        },

        // ── 智能理解 · Moondream2 ──

        new()

        {

            Id = "moondream2-vision",

            DisplayName = "Moondream2 · 视觉编码器",

            UsageHint = "轻量视觉语言模型（视觉部分）",

            Category = "智能理解",

            SortOrder = 20,

            RelativePath = "onnx/vision/moondream2_vision_encoder.onnx",

            DownloadUrl = "https://huggingface.co/Xenova/moondream2/resolve/main/onnx/vision_encoder.onnx",

            ApproximateSizeBytes = 1_757_000_000

        },

        new()

        {

            Id = "moondream2-text",

            DisplayName = "Moondream2 · 解码器",

            UsageHint = "轻量视觉语言模型（文本解码部分）",

            Category = "智能理解",

            SortOrder = 21,

            RelativePath = "onnx/vision/moondream2_decoder_fp16.onnx",

            DownloadUrl = "https://huggingface.co/Xenova/moondream2/resolve/main/onnx/decoder_model_merged_fp16.onnx",

            ApproximateSizeBytes = 203_712_385

        }

    ];



    public static IReadOnlyList<string> Categories { get; } =

        ["文字识别", "图像增强", "图像分割", "智能理解"];



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

        GetOcrModelsForTier(tier).All(IsDownloaded);



    public static IReadOnlyList<AiModelDescriptor> GetMattingModels(MattingModel model) =>

        model switch

        {

            MattingModel.Rmbg20 => [Require("rmbg-2")],

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

            SuperResolutionModel.X2 => [Require("realesrgan-x2")],

            _ => [Require("realesrgan")]

        };



    public static bool IsSuperResolutionReady(SuperResolutionModel model) =>

        GetSuperResolutionModels(model).All(IsDownloaded);



    public static string GetSuperResolutionModelPath(SuperResolutionModel model) =>

        GetAbsolutePath(GetSuperResolutionModels(model)[0]);



    public static int GetSuperResolutionScale(SuperResolutionModel model) =>

        model == SuperResolutionModel.X2 ? 2 : 4;



    public static IReadOnlyList<AiModelDescriptor> GetSegmentationModels(SegmentationModel model) =>

        model switch

        {

            SegmentationModel.FastSam => [Require("fastsam")],

            _ => [Require("mobilesam-encoder"), Require("mobilesam-decoder")]

        };



    public static bool IsSegmentationReady(SegmentationModel model) =>

        GetSegmentationModels(model).All(IsDownloaded);



    public static IReadOnlyList<AiModelDescriptor> GetVisionModels(VisionModel model) =>

        model switch

        {

            VisionModel.Moondream2 => [Require("moondream2-vision"), Require("moondream2-text")],

            _ => [Require("florence2-vision"), Require("florence2-decoder"), Require("florence2-encoder"), Require("florence2-embed")]

        };



    public static bool IsVisionReady(VisionModel model) =>

        GetVisionModels(model).All(IsDownloaded);



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

            "点选分割" => GetSegmentationModels(AiFeatureSettings.Segmentation),

            "AI 读图" => GetVisionModels(AiFeatureSettings.Vision),

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



    private static AiModelDescriptor Require(string id) =>

        FindById(id) ?? throw new InvalidOperationException($"未在模型目录中注册：{id}");

}


