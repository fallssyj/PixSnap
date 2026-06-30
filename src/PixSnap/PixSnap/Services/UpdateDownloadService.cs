using Serilog;
using System.IO;
using System.Net.Http;

namespace PixSnap.Services;

/// <summary>下载发行版安装包到系统临时目录。</summary>
public static class UpdateDownloadService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static string DownloadDirectory =>
        Path.Combine(Path.GetTempPath(), "PixSnap");

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream,*/*");
        return client;
    }

    public static string GetInstallerPath(string fileName) =>
        Path.Combine(DownloadDirectory, fileName);

    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string fileName,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("安装包下载地址为空。");

        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("安装包文件名为空。");

        Directory.CreateDirectory(DownloadDirectory);
        CleanupStaleDownloads(fileName);

        var destPath = GetInstallerPath(fileName);
        var tempPath = destPath + ".download";

        Log.Information("开始下载安装包: {Url} -> {Path}", downloadUrl, destPath);

        using var response = await Http
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
        Log.Information("安装包下载完成: {Path} ({Size} bytes)", destPath, downloaded);
        return destPath;
    }

    private static void CleanupStaleDownloads(string keepFileName)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(DownloadDirectory))
            {
                var name = Path.GetFileName(path);
                if (name.Equals(keepFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith("PixSnap-Setup", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理旧安装包缓存失败");
        }
    }
}
