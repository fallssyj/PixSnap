using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Models;
using PixSnap.Services;
using Serilog;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PixSnap.ViewModels;

public partial class AiModelsViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<AiModelGroupViewModel> _groups = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public AiModelsViewModel() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        Groups = new ObservableCollection<AiModelGroupViewModel>(
            AiModelCatalog.Categories.Select(category =>
            {
                var items = AiModelCatalog.GetByCategory(category)
                    .Select(m => new AiModelItemViewModel(m, RefreshSummary))
                    .ToList();
                return new AiModelGroupViewModel(category, items);
            }));
        RefreshSummary();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadMissing))]
    private async Task DownloadMissingAsync()
    {
        var pending = Groups.SelectMany(g => g.Items)
            .Where(m => m.ShowDownloadButton && m.IsFeatureSelectable)
            .ToList();
        if (pending.Count == 0)
            return;

        IsBusy = true;
        try
        {
            foreach (var item in pending)
                await item.DownloadModelAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            foreach (var group in Groups)
                group.RefreshSummary();
            RefreshSummary();
        }
    }

    private bool CanDownloadMissing() =>
        !IsBusy && Groups.SelectMany(g => g.Items).Any(m => m.ShowDownloadButton && m.IsFeatureSelectable);

    [RelayCommand]
    private void OpenModelsDirectory() => ShellHelper.OpenDirectory(AiModelCatalog.ModelsRootDirectory, "模型目录");

    partial void OnIsBusyChanged(bool value) => DownloadMissingCommand.NotifyCanExecuteChanged();

    private void RefreshSummary()
    {
        var all = Groups.SelectMany(g => g.Items).ToList();
        int downloaded = all.Count(m => m.IsDownloaded);
        SummaryText = $"共 {all.Count} 个模型，已就绪 {downloaded} 个";
        foreach (var group in Groups)
            group.RefreshSummary();
        DownloadMissingCommand.NotifyCanExecuteChanged();
    }
}

public sealed class AiModelGroupViewModel
{
    public AiModelGroupViewModel(string category, IReadOnlyList<AiModelItemViewModel> items)
    {
        Category = category;
        Items = new ObservableCollection<AiModelItemViewModel>(items);
        RefreshSummary();
    }

    public string Category { get; }

    public ObservableCollection<AiModelItemViewModel> Items { get; }

    public string ReadySummary { get; private set; } = string.Empty;

    public void RefreshSummary()
    {
        int ready = Items.Count(i => i.IsDownloaded);
        ReadySummary = $"{ready} / {Items.Count} 已就绪";
    }
}

public partial class AiModelItemViewModel : ObservableObject
{
    private readonly AiModelDescriptor _descriptor;
    private readonly Action _onChanged;

    public AiModelItemViewModel(AiModelDescriptor descriptor, Action onChanged)
    {
        _descriptor = descriptor;
        _onChanged = onChanged;
        RefreshState();
    }

    public string DisplayName => _descriptor.DisplayName;

    public string UsageHint => _descriptor.UsageHint ?? string.Empty;

    public string IntegrationLabel => AiModelCatalog.GetIntegrationLabel(_descriptor);

    public bool IsFeatureSelectable => AiModelCatalog.IsFeatureSelectable(_descriptor);

    public bool IsPlanned => !IsFeatureSelectable;

    public string FileName => Path.GetFileName(_descriptor.RelativePath);

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private Brush _statusBrush = Brushes.Gray;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatusText = string.Empty;

    public bool CanDownload => !string.IsNullOrWhiteSpace(_descriptor.DownloadUrl);

    public bool ShowDownloadButton => CanDownload && !IsDownloaded && !IsDownloading && IsFeatureSelectable;

    public bool ShowReadyMark => IsDownloaded && !IsDownloading;

    public void RefreshState()
    {
        IsDownloaded = AiModelCatalog.IsDownloaded(_descriptor);
        if (_descriptor.IsBundled)
        {
            StatusText = IsDownloaded ? "已内置" : "缺失";
            StatusBrush = IsDownloaded
                ? (Brush)Application.Current.FindResource("SystemFillColorSuccessBrush")
                : (Brush)Application.Current.FindResource("SystemFillColorCriticalBrush");
        }
        else if (!IsFeatureSelectable)
        {
            StatusText = IsDownloaded ? "已下载" : IntegrationLabel;
            StatusBrush = (Brush)Application.Current.FindResource("SystemFillColorNeutralBrush");
        }
        else if (AiModelCatalog.ResolveIntegrationStatus(_descriptor) == AiModelIntegrationStatus.Optional)
        {
            StatusText = IsDownloaded ? "可选·已就绪" : "可选";
            StatusBrush = IsDownloaded
                ? (Brush)Application.Current.FindResource("SystemFillColorSuccessBrush")
                : (Brush)Application.Current.FindResource("SystemFillColorNeutralBrush");
        }
        else if (IsDownloaded)
        {
            StatusText = "已就绪";
            StatusBrush = (Brush)Application.Current.FindResource("SystemFillColorSuccessBrush");
        }
        else
        {
            StatusText = "未下载";
            StatusBrush = (Brush)Application.Current.FindResource("SystemFillColorCautionBrush");
        }

        SizeText = AiModelCatalog.FormatSize(AiModelCatalog.GetFileSizeBytes(_descriptor));
        NotifyActionVisibility();
    }

    [RelayCommand(CanExecute = nameof(CanStartDownload))]
    private Task Download() => DownloadModelAsync();

    public async Task DownloadModelAsync()
    {
        if (!CanDownload)
            return;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = "0%";
        NotifyActionVisibility();
        try
        {
            var progress = new Progress<double>(v =>
            {
                DownloadProgress = v;
                DownloadStatusText = v >= 1 ? "完成" : $"{v * 100:0}%";
            });

            await AiModelDownloadService.DownloadAsync(_descriptor, progress).ConfigureAwait(true);
            RefreshState();
            _onChanged();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "下载模型失败: {Model}", _descriptor.DisplayName);
            AppMessageBox.Show(
                $"下载失败：{_descriptor.DisplayName}\n{ex.Message}",
                "PixSnap",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            IsDownloading = false;
            DownloadStatusText = string.Empty;
            NotifyActionVisibility();
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStartDownload() => ShowDownloadButton;

    private void NotifyActionVisibility()
    {
        OnPropertyChanged(nameof(ShowDownloadButton));
        OnPropertyChanged(nameof(ShowReadyMark));
    }

    partial void OnIsDownloadedChanged(bool value)
    {
        NotifyActionVisibility();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDownloadingChanged(bool value) => NotifyActionVisibility();
}
