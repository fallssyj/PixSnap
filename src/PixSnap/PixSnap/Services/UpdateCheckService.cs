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
    private const string GiteeOwner = "falls_syj";
    private const string GiteeRepo = "PixSnap";

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
                    Source: source,
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
                    Source: source,
                    CurrentVersion: currentDisplay,
                    LatestVersion: release.TagName,
                    DownloadUrl: null,
                    InstallerFileName: null,
                    Message: $"无法识别版本号：{release.TagName}");
            }

            var (downloadUrl, installerFileName) = await FindInstallerDownloadAsync(release, source, cancellationToken)
                .ConfigureAwait(false);
            var latestDisplay = FormatVersion(latestVersion);
            var hasUpdate = latestVersion > current;

            Log.Information(
                "检查更新 ({Source}): 当前 {Current}, 最新 {Latest}, 有更新={HasUpdate}, 安装包={Installer}",
                source,
                currentDisplay,
                latestDisplay,
                hasUpdate,
                installerFileName ?? "(未找到)");

            return new UpdateCheckResult(
                IsSuccess: true,
                HasUpdate: hasUpdate,
                Source: source,
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
                Source: source,
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
                OpenDownloadUrl(result.DownloadUrl, result.LatestVersion, result.Source);
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
            OpenDownloadUrl(null, result.LatestVersion, result.Source);
            return;
        }

        var fileName = result.InstallerFileName
            ?? ResolveInstallerFileName(result.DownloadUrl, result.LatestVersion);

        var window = new UpdateDownloadWindow(owner, result.Source, result.DownloadUrl, fileName, result.LatestVersion);
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

    public static void OpenDownloadUrl(string? downloadUrl, string? latestVersion, UpdateSource? source = null)
    {
        var url = downloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = GetReleasesPageUrl(source ?? SettingsService.ReadUpdateSource());

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

    private static string GetReleasesPageUrl(UpdateSource source) =>
        source == UpdateSource.Gitee
            ? "https://gitee.com/falls_syj/PixSnap/releases"
            : "https://github.com/fallssyj/PixSnap/releases";

    internal static Version GetCurrentVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var semverCore = informational.Split('+', 2)[0];
            if (TryParseVersion(semverCore, out var parsed))
                return parsed;
        }

        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
    }

    internal static string FormatVersion(Version version) =>
        version.Revision > 0
            ? $"v{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"v{version.Major}.{version.Minor}.{version.Build}";

    internal static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out version!);
    }

    private static async Task<(string? Url, string? FileName)> FindInstallerDownloadAsync(
        ReleaseDto release,
        UpdateSource source,
        CancellationToken cancellationToken)
    {
        var fromAssets = FindInstallerAsset(MapReleaseFiles(release.Assets, release.Id, source));
        if (fromAssets.Url is not null)
            return fromAssets;

        var attachFiles = release.AttachFiles;
        if ((attachFiles is null || attachFiles.Count == 0) && source == UpdateSource.Gitee && release.Id > 0)
        {
            attachFiles = await FetchGiteeAttachFilesAsync(release.Id, cancellationToken).ConfigureAwait(false);
        }

        return FindInstallerAsset(MapReleaseFiles(attachFiles, release.Id, source));
    }

    private static async Task<List<ReleaseFileDto>?> FetchGiteeAttachFilesAsync(int releaseId, CancellationToken cancellationToken)
    {
        var url = $"https://gitee.com/api/v5/repos/{GiteeOwner}/{GiteeRepo}/releases/{releaseId}/attach_files";
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("获取 Gitee attach_files 失败: HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<List<ReleaseFileDto>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取 Gitee attach_files 失败: ReleaseId={ReleaseId}", releaseId);
            return null;
        }
    }

    private static IEnumerable<ReleaseFileDto> MapReleaseFiles(
        IReadOnlyList<ReleaseFileDto>? files,
        int releaseId,
        UpdateSource source)
    {
        if (files is null)
            yield break;

        foreach (var file in files)
        {
            yield return new ReleaseFileDto
            {
                Id = file.Id,
                Name = file.Name ?? file.FileName,
                FileName = file.FileName,
                BrowserDownloadUrl = ResolveFileDownloadUrl(file, releaseId, source)
            };
        }
    }

    private static string? ResolveFileDownloadUrl(ReleaseFileDto file, int releaseId, UpdateSource source)
    {
        if (!string.IsNullOrWhiteSpace(file.BrowserDownloadUrl))
            return file.BrowserDownloadUrl;

        if (source == UpdateSource.Gitee && releaseId > 0 && file.Id > 0)
        {
            return $"https://gitee.com/api/v5/repos/{GiteeOwner}/{GiteeRepo}/releases/{releaseId}/attach_files/{file.Id}/download";
        }

        return null;
    }

    private static (string? Url, string? FileName) FindInstallerAsset(IEnumerable<ReleaseFileDto> files)
    {
        ReleaseFileDto? setupMatch = null;
        ReleaseFileDto? exeMatch = null;

        foreach (var file in files)
        {
            var name = file.Name ?? file.FileName;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(file.BrowserDownloadUrl))
                continue;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Contains("PixSnap-Setup", StringComparison.OrdinalIgnoreCase))
            {
                setupMatch = file;
                break;
            }

            exeMatch ??= file;
        }

        var match = setupMatch ?? exeMatch;
        if (match is null)
            return (null, null);

        return (match.BrowserDownloadUrl, match.Name ?? match.FileName);
    }

    private static string ResolveInstallerFileName(string downloadUrl, string? latestVersion)
    {
        try
        {
            var name = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            if (!string.IsNullOrWhiteSpace(name) && !name.Equals("download", StringComparison.OrdinalIgnoreCase))
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
        public int Id { get; set; }

        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        public List<ReleaseFileDto>? Assets { get; set; }

        [JsonPropertyName("attach_files")]
        public List<ReleaseFileDto>? AttachFiles { get; set; }
    }

    private sealed class ReleaseFileDto
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public string? FileName { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool IsSuccess,
    bool HasUpdate,
    UpdateSource Source,
    string CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    string? InstallerFileName,
    string Message);
