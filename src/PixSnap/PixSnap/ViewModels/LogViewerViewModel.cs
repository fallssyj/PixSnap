using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;

namespace PixSnap.ViewModels;

public sealed class LogDateGroup
{
    public required string Header { get; init; }
    public ObservableCollection<LogDateGroup>? Children { get; init; }
    public string? FilePath { get; init; }
    public bool IsExpanded { get; init; }
}

public partial class LogViewerViewModel : ObservableObject
{
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    public ObservableCollection<LogDateGroup> LogTree { get; } = [];

    [ObservableProperty]
    private string _logContent = string.Empty;

    [ObservableProperty]
    private string _selectedFileName = string.Empty;

    public LogViewerViewModel()
    {
        LoadLogTree();
    }

    public void SelectLogFile(string? filePath)
    {
        if (filePath is null || !File.Exists(filePath)) return;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            LogContent = reader.ReadToEnd();
            SelectedFileName = Path.GetFileName(filePath);
        }
        catch
        {
            LogContent = "无法读取文件。";
        }
    }

    partial void OnSelectedLogItemChanged(LogDateGroup? value)
    {
        if (value?.FilePath is { } path)
            SelectLogFile(path);
    }

    [ObservableProperty]
    private LogDateGroup? _selectedLogItem;

    private void LoadLogTree()
    {
        if (!Directory.Exists(LogDirectory)) return;

        // 目录结构: logs/yyyy-MM-dd/pixsnap.log
        var subDirs = Directory.GetDirectories(LogDirectory)
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
        var todayDay = today.ToString("dd");

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

        // native crash 日志（C++ 写出，位于 logs/native_crash.log）
        var nativeCrashLog = Path.Combine(LogDirectory, "native_crash.log");
        if (File.Exists(nativeCrashLog))
        {
            LogTree.Add(new LogDateGroup
            {
                Header = "native_crash.log",
                FilePath = nativeCrashLog
            });
        }

        SelectedLogItem = FindFirstLogFile(LogTree);
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
