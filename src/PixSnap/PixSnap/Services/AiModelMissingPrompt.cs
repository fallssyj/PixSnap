using PixSnap.Models;
using PixSnap.ViewModels;
using PixSnap.Views;
using Serilog;
using System.IO;
using System.Windows;

namespace PixSnap.Services;

/// <summary>模型缺失时提示用户下载或打开模型管理。</summary>
internal static class AiModelMissingPrompt
{
    public static async Task HandleFeatureNotAvailableAsync(
        string featureName,
        string message,
        Window? owner = null)
    {
        owner = WindowOwnerHelper.GetActiveOwner(owner);
        var missing = AiModelCatalog.GetModelsForFeature(featureName)
            .Where(m => !AiModelCatalog.IsDownloaded(m) && !string.IsNullOrWhiteSpace(m.DownloadUrl))
            .ToList();

        if (missing.Count > 0)
        {
            await ConfirmAndDownloadAsync(missing, featureName, owner).ConfigureAwait(true);
            return;
        }

        AppMessageBox.Show(message, featureName, MessageBoxButton.OK, MessageBoxImage.Warning, owner);
    }

    public static void OpenModelManager(Window? owner = null)
    {
        owner = WindowOwnerHelper.GetActiveOwner(owner);
        var win = new AiModelsWindow(new AiModelsViewModel());
        if (owner is { IsVisible: true })
            win.Owner = owner;
        win.ShowDialog();
    }

    public static async Task HandleFileNotFoundAsync(
        FileNotFoundException ex,
        string featureName,
        Window? owner = null)
    {
        owner = WindowOwnerHelper.GetActiveOwner(owner);
        var models = AiModelCatalog.GetModelsForFeature(featureName)
            .Where(m => !AiModelCatalog.IsDownloaded(m) && !string.IsNullOrWhiteSpace(m.DownloadUrl))
            .ToList();

        if (models.Count > 0)
        {
            await ConfirmAndDownloadAsync(models, featureName, owner).ConfigureAwait(true);
            return;
        }

        var model = ResolveModel(ex, featureName);
        if (model is null || string.IsNullOrWhiteSpace(model.DownloadUrl))
        {
            Log.Warning(
                "无法解析缺失模型: Feature={Feature}, FileName={FileName}, Message={Message}",
                featureName,
                ex.FileName,
                ex.Message);
            AppMessageBox.Show(
                $"AI 模型文件缺失，无法执行{featureName}。\n\n{ex.Message}",
                "模型缺失",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                owner);
            return;
        }

        await ConfirmAndDownloadAsync([model], featureName, owner).ConfigureAwait(true);
    }

    public static async Task HandleOcrNotAvailableAsync(
        OcrService.OcrNotAvailableException ex,
        Window? owner = null)
    {
        owner = WindowOwnerHelper.GetActiveOwner(owner);
        var missing = AiModelCatalog.GetOcrModelsForTier(OcrSettings.Tier)
            .Where(m => !AiModelCatalog.IsDownloaded(m))
            .ToList();

        var downloadable = missing
            .Where(m => !string.IsNullOrWhiteSpace(m.DownloadUrl))
            .ToList();

        if (downloadable.Count > 0)
        {
            await ConfirmAndDownloadAsync(downloadable, "文字识别", owner).ConfigureAwait(true);
            return;
        }

        if (missing.Count > 0)
        {
            string names = string.Join("\n", missing.Select(m => $"· {m.DisplayName}"));
            AppMessageBox.Show(
                $"OCR 模型未就绪，缺少：\n\n{names}\n\n请重新编译安装，或在「模型管理」中检查文件。",
                "扫描文本不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                owner);
            return;
        }

        AppMessageBox.Show(ex.Message, "扫描文本不可用", MessageBoxButton.OK, MessageBoxImage.Warning, owner);
    }

    private static async Task ConfirmAndDownloadAsync(
        IReadOnlyList<AiModelDescriptor> models,
        string featureName,
        Window? owner)
    {
        string modelLines = string.Join("\n", models.Select(m =>
            $"· {m.DisplayName}（约 {AiModelCatalog.FormatSize(m.ApproximateSizeBytes)}）"));

        var result = AppMessageBox.Show(
            $"缺少以下模型，无法执行{featureName}：\n\n{modelLines}\n\n是否现在下载？",
            "模型缺失",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            owner);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            foreach (var model in models)
            {
                Log.Information("用户确认下载缺失模型: {Model}", model.DisplayName);
                await AiModelDownloadService.DownloadAsync(model).ConfigureAwait(true);
            }

            AppMessageBox.Show(
                models.Count == 1
                    ? $"「{models[0].DisplayName}」已下载完成，请重新执行操作。"
                    : $"已下载 {models.Count} 个模型，请重新执行操作。",
                "下载完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                owner);
        }
        catch (Exception downloadEx)
        {
            Log.Warning(downloadEx, "模型下载失败");
            var retry = AppMessageBox.Show(
                $"下载失败：{downloadEx.Message}\n\n是否打开「模型管理」手动下载？",
                "下载失败",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                owner);
            if (retry == MessageBoxResult.Yes)
                OpenModelManager(owner);
        }
    }

    private static AiModelDescriptor? ResolveModel(FileNotFoundException ex, string featureName)
    {
        if (!string.IsNullOrWhiteSpace(ex.FileName))
        {
            var byPath = AiModelCatalog.FindByAbsolutePath(ex.FileName);
            if (byPath is not null)
                return byPath;

            var byName = AiModelCatalog.FindByFileName(Path.GetFileName(ex.FileName));
            if (byName is not null)
                return byName;
        }

        foreach (string marker in new[] { "未找到 ONNX 模型：", "未找到 ONNX 模型:" })
        {
            int index = ex.Message.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                continue;

            string path = ex.Message[(index + marker.Length)..].Trim();
            var fromMessage = AiModelCatalog.FindByAbsolutePath(path)
                ?? AiModelCatalog.FindByFileName(Path.GetFileName(path));
            if (fromMessage is not null)
                return fromMessage;
        }

        if (FeatureModelIds.TryGetValue(featureName, out string? modelId))
            return AiModelCatalog.FindById(modelId);

        var configured = AiModelCatalog.GetModelsForFeature(featureName);
        return configured.FirstOrDefault(m => !AiModelCatalog.IsDownloaded(m)) ?? configured.FirstOrDefault();
    }

    private static readonly Dictionary<string, string> FeatureModelIds =
        new(StringComparer.Ordinal)
        {
            ["AI 擦除"] = "lama"
        };
}
