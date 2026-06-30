using PixSnap.Services;
using Serilog;
using System.Diagnostics;
using System.Windows;

namespace PixSnap.Views;

public partial class UpdateDownloadWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _downloadUrl;
    private readonly string _fileName;
    private bool _isClosing;

    public UpdateDownloadWindow(Window? owner, string downloadUrl, string fileName, string? latestVersion)
    {
        Owner = WindowOwnerHelper.GetActiveOwner(owner);
        _downloadUrl = downloadUrl;
        _fileName = fileName;

        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(latestVersion))
            VersionText.Text = $"版本 {latestVersion}";

        DownloadProgress.IsIndeterminate = true;
        StatusText.Text = "正在下载...";

        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            if (!_isClosing)
                _cts.Cancel();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var progress = new Progress<double>(ReportProgress);
            var installerPath = await UpdateDownloadService
                .DownloadInstallerAsync(_downloadUrl, _fileName, progress, _cts.Token)
                .ConfigureAwait(true);

            StatusText.Text = "下载完成，正在启动安装程序...";
            DownloadProgress.Value = 100;
            LaunchInstaller(installerPath);
            CloseSafely();
        }
        catch (OperationCanceledException)
        {
            CloseSafely();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "安装包下载失败");
            StatusText.Text = "下载失败";
            CancelButton.Content = "关闭";

            var retry = AppMessageBox.Show(
                "安装包下载失败，请检查网络后重试，或在浏览器中打开下载页。\n\n是否在浏览器中打开？",
                "下载更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                Owner);

            if (retry == MessageBoxResult.Yes)
                UpdateCheckService.OpenDownloadUrl(_downloadUrl, null);

            CloseSafely();
        }
    }

    private void ReportProgress(double value)
    {
        if (value >= 1)
        {
            DownloadProgress.Value = 100;
            StatusText.Text = "100%";
            return;
        }

        var percent = value * 100;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = percent;
        StatusText.Text = $"{percent:0}%";
    }

    private static void LaunchInstaller(string installerPath)
    {
        Log.Information("启动安装程序: {Path}", installerPath);
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CloseSafely();
    }

    private void CloseSafely()
    {
        _isClosing = true;
        Close();
    }
}
