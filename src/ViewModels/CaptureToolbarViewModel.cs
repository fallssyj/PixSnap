// 编码：UTF-8 BOM
// 截图工具栏 ViewModel，管理截图模式选择和截图操作

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Models;
using PixSnap.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;

namespace PixSnap.ViewModels;

/// <summary>截图工具栏 ViewModel</summary>
public partial class CaptureToolbarViewModel : ObservableObject
{
    private readonly CaptureService _captureService = new();

    /// <summary>可选的截图模式列表</summary>
    public ObservableCollection<CaptureModeItem> CaptureModes { get; } = new()
    {
        new CaptureModeItem { Mode = CaptureMode.Rectangle, DisplayName = "矩形截图" },
        new CaptureModeItem { Mode = CaptureMode.Fullscreen, DisplayName = "全屏截图" },
        new CaptureModeItem { Mode = CaptureMode.Window,    DisplayName = "窗口截图"  }
    };

    [ObservableProperty]
    private CaptureModeItem _selectedCaptureMode;

    /// <summary>截图完成后的回调，传递截图结果</summary>
    public event Action<BitmapSource>? CaptureCompleted;

    /// <summary>请求关闭工具栏（取消截图）</summary>
    public event Action? CancelRequested;

    public CaptureToolbarViewModel()
    {
        _selectedCaptureMode = CaptureModes[0]; // 默认矩形
    }

    /// <summary>开始截图命令（从工具栏按钮触发）</summary>
    [RelayCommand]
    private void StartCapture()
    {
        // 具体截图逻辑由 View 层配合叠加窗口实现
        // 此处仅通知 View 层以哪种模式启动
        OnStartCaptureRequested?.Invoke(SelectedCaptureMode.Mode);
    }

    /// <summary>取消截图命令</summary>
    [RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }

    /// <summary>请求以指定模式开始截图的事件</summary>
    public event Action<CaptureMode>? OnStartCaptureRequested;

    /// <summary>执行全屏截图并触发完成事件</summary>
    public void DoFullscreenCapture()
    {
        try
        {
            var bitmap = _captureService.CaptureFullscreen();
            CaptureCompleted?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"全屏截图失败：{ex.Message}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>执行区域截图（screenX/Y/W/H 为物理像素坐标）</summary>
    public void DoRegionCapture(int x, int y, int width, int height)
    {
        try
        {
            var bitmap = _captureService.CaptureRegion(x, y, width, height);
            CaptureCompleted?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"区域截图失败：{ex.Message}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>执行窗口截图</summary>
    public void DoWindowCapture(IntPtr hWnd)
    {
        try
        {
            var bitmap = _captureService.CaptureWindow(hWnd);
            if (bitmap != null)
                CaptureCompleted?.Invoke(bitmap);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"窗口截图失败：{ex.Message}", "PixSnap", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>获取可见窗口列表（用于窗口截图选择）</summary>
    public List<WindowInfo> GetWindowList() => _captureService.EnumerateWindows();
}

/// <summary>截图模式选项（用于下拉绑定）</summary>
public class CaptureModeItem
{
    public CaptureMode Mode { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
