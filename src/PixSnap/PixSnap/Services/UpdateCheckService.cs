using PixSnap.Models;
using PixSnap.Views;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace PixSnap.Services;

/// <summary>从 GitHub / Gitee 发行版检查更新。</summary>
public static class UpdateCheckService
{
    private const string GitHubLatestUrl = "https://api.github.com/repos/fallssyj/PixSnap/releases/latest";
    private const string GiteeLatestUrl = "https://gitee.com/api/v5/repos/falls_syj/PixSnap/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static UpdateCheckService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("PixSnap-UpdateCheck");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static string CurrentVersionDisplay => FormatVersion(GetCurrentVersion());

    public static async Task<UpdateCheckResult> CheckAsync(
        UpdateSource source,
        CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersion();
        var currentDisplay = FormatVersion(current);

        try
        {
            var url = source == UpdateSource.Gitee ? GiteeLatestUrl : GitHubLatestUrl;
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<ReleaseDto>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(
                    IsSuccess: false,
                    HasUpdate: false,
                    CurrentVersion: currentDisplay,
                    LatestVersion: null,
                    DownloadUrl: null,
                    InstallerFileName: null,
                    Message: "无法解析发行版信息。");
            }

            if (!TryParseVersion(release.TagName, out var latestVersion))
            {
                return new UpdateCheckResult(
                    IsSuccess: false,
                    HasUpdate: false,
                    CurrentVersion: currentDisplay,
                    LatestVersion: release.TagName,
                    DownloadUrl: null,
                    InstallerFileName: null,
                    Message: $"无法识别版本号：{release.TagName}");
            }

            var (downloadUrl, installerFileName) = FindInstallerAsset(release.Assets);
            var latestDisplay = FormatVersion(latestVersion);
            var hasUpdate = latestVersion > current;

            Log.Information(
                "检查更新 ({Source}): 当前 {Current}, 最新 {Latest}, 有更新={HasUpdate}",
                source,
                currentDisplay,
                latestDisplay,
                hasUpdate);

            return new UpdateCheckResult(
                IsSuccess: true,
                HasUpdate: hasUpdate,
                CurrentVersion: currentDisplay,
                LatestVersion: latestDisplay,
                DownloadUrl: downloadUrl,
                InstallerFileName: installerFileName,
                Message: hasUpdate
                    ? $"发现新版本 {latestDisplay}（当前 {currentDisplay}）"
                    : $"已是最新版本（{currentDisplay}）");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查更新失败: {Source}", source);
            return new UpdateCheckResult(
                IsSuccess: false,
                HasUpdate: false,
                CurrentVersion: currentDisplay,
                LatestVersion: null,
                DownloadUrl: null,
                InstallerFileName: null,
                Message: "检查更新失败，请稍后重试或检查网络连接。");
        }
    }

    public static void PromptUpdate(UpdateCheckResult result, Window? owner = null)
    {
        if (!result.HasUpdate)
            return;

        var message = result.Message
            + "\n\n「是」应用内下载并安装"
            + "\n「否」在浏览器中打开下载页"
            + "\n「取消」稍后";

        var choice = AppMessageBox.Show(
            message,
            "发现新版本",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Information,
            owner);

        switch (choice)
        {
            case MessageBoxResult.Yes:
                DownloadAndInstall(result, owner);
                break;
            case MessageBoxResult.No:
                OpenDownloadUrl(result.DownloadUrl, result.LatestVersion);
                break;
        }
    }

    public static void DownloadAndInstall(UpdateCheckResult result, Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            AppMessageBox.Show(
                "未找到安装包下载地址，将打开发行版页面。",
                "下载更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                owner);
            OpenDownloadUrl(null, result.LatestVersion);
            return;
        }

        var fileName = result.InstallerFileName
            ?? ResolveInstallerFileName(result.DownloadUrl, result.LatestVersion);

        var window = new UpdateDownloadWindow(owner, result.DownloadUrl, fileName, result.LatestVersion);
        window.ShowDialog();
    }

    public static void ShowManualCheckResult(UpdateCheckResult result, Window? owner = null)
    {
        if (!result.IsSuccess)
        {
            AppMessageBox.Show(result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning, owner);
            return;
        }

        if (result.HasUpdate)
        {
            PromptUpdate(result, owner);
            return;
        }

        AppMessageBox.Show(result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information, owner);
    }

    public static void OpenDownloadUrl(string? downloadUrl, string? latestVersion)
    {
        var url = downloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = SettingsService.ReadUpdateSource() == UpdateSource.Gitee
                ? "https://gitee.com/falls_syj/PixSnap/releases"
                : "https://github.com/fallssyj/PixSnap/releases";
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开更新下载页失败: {Url}", url);
            AppMessageBox.Show($"无法打开下载链接：\n{url}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v ?? new Version(1, 0, 0);
    }

    private static string FormatVersion(Version version) => $"v{version.Major}.{version.Minor}.{version.Build}";

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out version!);
    }

    private static (string? Url, string? FileName) FindInstallerAsset(IReadOnlyList<ReleaseAssetDto>? assets)
    {
        if (assets is null || assets.Count == 0)
            return (null, null);

        foreach (var asset in assets)
        {
            if (asset.Name is not null
                && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && asset.Name.Contains("PixSnap-Setup", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                return (asset.BrowserDownloadUrl, asset.Name);
            }
        }

        foreach (var asset in assets)
        {
            if (asset.Name is not null
                && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                return (asset.BrowserDownloadUrl, asset.Name);
            }
        }

        return (null, null);
    }

    private static string ResolveInstallerFileName(string downloadUrl, string? latestVersion)
    {
        try
        {
            var name = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
            // 忽略 URL 解析失败，使用版本号回退文件名。
        }

        var versionText = latestVersion?.Trim().TrimStart('v', 'V') ?? "latest";
        return $"PixSnap-Setup-{versionText}-x64.exe";
    }

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        public List<ReleaseAssetDto>? Assets { get; set; }
    }

    private sealed class ReleaseAssetDto
    {
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool IsSuccess,
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    string? InstallerFileName,
    string Message);
