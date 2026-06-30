using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PixSnap.ViewModels;

public sealed class LogDateGroup
{
    public required string Header { get; init; }
    public ObservableCollection<LogDateGroup>? Children { get; init; }
    public string? FilePath { get; init; }
    public bool IsExpanded { get; init; }
}

public partial class LogViewerViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _refreshTimer;
    private string? _loadedFilePath;
    private long _lastFileLength = -1;
    private DateTime _lastTreeRefreshUtc = DateTime.MinValue;

    public static string LogsDirectory => LogFileService.LogsDirectory;

    public ObservableCollection<LogDateGroup> LogTree { get; } = [];

    /// <summary>请求将日志文本框滚动到底部（由 View 订阅）。</summary>
    public event Action? RequestScrollToEnd;

    [ObservableProperty]
    private string _logContent = string.Empty;

    [ObservableProperty]
    private string _selectedFileName = string.Empty;

    public LogViewerViewModel()
    {
        LoadLogTree();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => OnRefreshTick();
        _refreshTimer.Start();
    }

    public void Dispose() => _refreshTimer.Stop();

    [RelayCommand]
    private void OpenLogDirectory() => ShellHelper.OpenDirectory(LogsDirectory, "日志目录");

    [RelayCommand]
    private void ClearLogs()
    {
        if (AppMessageBox.Show(
                "确定清空 logs 目录下的所有日志文件？\n将尝试删除包括当前正在写入的日志，并移除已清空的日期文件夹。",
                "PixSnap",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var (deleted, failed) = LogFileService.DeleteAllFiles();

        _loadedFilePath = null;
        _lastFileLength = -1;
        LogContent = string.Empty;
        SelectedFileName = string.Empty;
        LoadLogTree();

        if (failed > 0)
        {
            AppMessageBox.Show(
                $"已删除 {deleted} 个文件，{failed} 个文件仍未能删除。",
                "PixSnap",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    public void SelectLogFile(string? filePath, bool scrollToEnd = false)
    {
        if (filePath is null || !File.Exists(filePath))
        {
            _loadedFilePath = null;
            _lastFileLength = -1;
            return;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            LogContent = reader.ReadToEnd();
            SelectedFileName = Path.GetFileName(filePath);
            _loadedFilePath = filePath;
            _lastFileLength = stream.Length;
            if (scrollToEnd)
                RequestScrollToEnd?.Invoke();
        }
        catch
        {
            LogContent = "无法读取文件。";
            _loadedFilePath = null;
            _lastFileLength = -1;
        }
    }

    partial void OnSelectedLogItemChanged(LogDateGroup? value)
    {
        _loadedFilePath = null;
        _lastFileLength = -1;
        if (value?.FilePath is { } path)
            SelectLogFile(path, scrollToEnd: true);
    }

    [ObservableProperty]
    private LogDateGroup? _selectedLogItem;

    private void OnRefreshTick()
    {
        RefreshSelectedFileIfChanged();
        MaybeRefreshTree();
    }

    private void RefreshSelectedFileIfChanged()
    {
        var path = SelectedLogItem?.FilePath;
        if (path is null || !File.Exists(path))
            return;

        try
        {
            var info = new FileInfo(path);
            if (path == _loadedFilePath && info.Length == _lastFileLength)
                return;

            if (path != _loadedFilePath || info.Length < _lastFileLength)
            {
                SelectLogFile(path, scrollToEnd: ShouldFollowTail(path));
                return;
            }

            AppendTail(path, info.Length);
        }
        catch
        {
            // 文件被替换或短暂锁定时忽略，下一轮再试
        }
    }

    private void AppendTail(string filePath, long newLength)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(_lastFileLength, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var chunk = reader.ReadToEnd();
        _lastFileLength = newLength;
        _loadedFilePath = filePath;

        if (string.IsNullOrEmpty(chunk))
            return;

        LogContent += chunk;
        if (ShouldFollowTail(filePath))
            RequestScrollToEnd?.Invoke();
    }

    private static bool ShouldFollowTail(string filePath)
    {
        var todayLog = Path.Combine(LogsDirectory, DateTime.Now.ToString("yyyy-MM-dd"), "pixsnap.log");
        return string.Equals(filePath, todayLog, StringComparison.OrdinalIgnoreCase);
    }

    private void MaybeRefreshTree()
    {
        if ((DateTime.UtcNow - _lastTreeRefreshUtc).TotalSeconds < 15)
            return;

        _lastTreeRefreshUtc = DateTime.UtcNow;

        var todayLog = Path.Combine(LogsDirectory, DateTime.Now.ToString("yyyy-MM-dd"), "pixsnap.log");
        if (!File.Exists(todayLog))
            return;

        if (FindLogFileByPath(LogTree, todayLog) is not null)
            return;

        var keepSelection = SelectedLogItem?.FilePath;
        LoadLogTree(keepSelection);
    }

    private void LoadLogTree(string? selectFilePath = null)
    {
        LogTree.Clear();

        if (!Directory.Exists(LogsDirectory))
        {
            SelectedLogItem = null;
            return;
        }

        // 目录结构: logs/yyyy-MM-dd/pixsnap.log
        var subDirs = Directory.GetDirectories(LogsDirectory)
            .Select(d => new { Path = d, Name = Path.GetFileName(d) })
            .Where(d => d.Name.Length == 10 && DateOnly.TryParse(d.Name, out _))
            .OrderByDescending(d => d.Name);

        var yearGroups = new SortedDictionary<string, SortedDictionary<string, List<(string Day, string FilePath)>>>(Comparer<string>.Create((a, b) => b.CompareTo(a)));

        foreach (var dir in subDirs)
        {
            var parts = dir.Name.Split('-');
            if (parts.Length != 3) continue;
            var year = parts[0];
            var month = parts[1];
            var day = parts[2];

            var logFiles = Directory.GetFiles(dir.Path, "*.log");
            if (logFiles.Length == 0) continue;

            if (!yearGroups.TryGetValue(year, out var months))
                yearGroups[year] = months = new SortedDictionary<string, List<(string, string)>>(Comparer<string>.Create((a, b) => b.CompareTo(a)));
            if (!months.TryGetValue(month, out var days))
                months[month] = days = [];

            foreach (var logFile in logFiles)
                days.Add((day, logFile));
        }

        var today = DateTime.Now;
        var todayYear = today.ToString("yyyy");
        var todayMonth = today.ToString("MM");

        foreach (var (year, months) in yearGroups)
        {
            var isCurrentYear = year == todayYear;
            var yearNode = new LogDateGroup
            {
                Header = $"{year}",
                Children = [],
                IsExpanded = isCurrentYear
            };
            foreach (var (month, days) in months)
            {
                var isCurrentMonth = isCurrentYear && month == todayMonth;
                var monthNode = new LogDateGroup
                {
                    Header = $"{month}",
                    Children = [],
                    IsExpanded = isCurrentMonth
                };
                foreach (var (day, filePath) in days.OrderByDescending(d => d.Day))
                {
                    var fileName = Path.GetFileName(filePath);
                    var label = days.Count(d => d.Day == day) > 1 ? $"{day} - {fileName}" : $"{day}";
                    monthNode.Children.Add(new LogDateGroup
                    {
                        Header = label,
                        FilePath = filePath
                    });
                }
                yearNode.Children!.Add(monthNode);
            }
            LogTree.Add(yearNode);
        }

        var nativeCrashLog = Path.Combine(LogsDirectory, "native_crash.log");
        if (File.Exists(nativeCrashLog))
        {
            LogTree.Add(new LogDateGroup
            {
                Header = "native_crash.log",
                FilePath = nativeCrashLog
            });
        }

        var nextSelection = selectFilePath is not null && File.Exists(selectFilePath)
            ? FindLogFileByPath(LogTree, selectFilePath) ?? FindFirstLogFile(LogTree)
            : FindFirstLogFile(LogTree);

        if (nextSelection?.FilePath != SelectedLogItem?.FilePath)
            SelectedLogItem = nextSelection;
        else if (nextSelection?.FilePath is { } samePath)
            SelectLogFile(samePath, scrollToEnd: false);
    }

    private static LogDateGroup? FindLogFileByPath(IEnumerable<LogDateGroup> nodes, string filePath)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.FilePath) &&
                string.Equals(node.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return node;

            if (node.Children is { Count: > 0 } children)
            {
                var nested = FindLogFileByPath(children, filePath);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static LogDateGroup? FindFirstLogFile(IEnumerable<LogDateGroup> nodes)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.FilePath))
                return node;

            if (node.Children is { Count: > 0 } children)
            {
                var nested = FindFirstLogFile(children);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }
}
