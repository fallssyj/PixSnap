using PixSnap.Models;
using Serilog;
using System.IO;
using System.Net.Http;

namespace PixSnap.Services;

/// <summary>从远程地址下载 AI ONNX 模型到用户数据目录。</summary>
public static class AiModelDownloadService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        return client;
    }

    public static async Task DownloadAsync(
        AiModelDescriptor model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model.DownloadUrl))
            throw new InvalidOperationException($"{model.DisplayName} 不支持在线下载。");

        var downloadUrl = ModelDownloadUrlHelper.Resolve(model.DownloadUrl);
        var destPath = AiModelCatalog.GetDownloadPath(model);
        var directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = destPath + ".download";
        Log.Information("开始下载模型: {Name} -> {Path} ({Url})", model.DisplayName, destPath, downloadUrl);

        using var response = await HttpClient
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            if (totalBytes > 0)
                progress?.Report(downloaded / (double)totalBytes);
        }

        target.Close();
        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tempPath, destPath);

        progress?.Report(1.0);
        Log.Information("模型下载完成: {Name} ({Size})", model.DisplayName, AiModelCatalog.FormatSize(downloaded));
    }
}
