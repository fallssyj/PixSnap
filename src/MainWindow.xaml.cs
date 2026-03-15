// 编码：UTF-8 BOM
// 主窗口后台代码：处理设置操作、托盘交互

using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PixSnap;

/// <summary>主窗口（设置窗口），双击托盘图标时显示</summary>
public partial class MainWindow : HandyControl.Controls.Window
{
    private MainViewModel? _mainVm;

    /// <summary>请求呼出截图操作</summary>
    public event Action? CaptureRequested;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
    }

    /// <summary>注入主 ViewModel</summary>
    public void SetViewModel(MainViewModel vm)
    {
        _mainVm = vm;
        DataContext = vm;
        UpdateHotkeyDisplay();
    }

    /// <summary>窗口加载完成后初始化热键服务</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_mainVm != null)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _mainVm.InitializeHotkey(hwnd);
        }
    }

    private void UpdateHotkeyDisplay()
    {
        if (_mainVm != null)
        {
            TbHotkey.Text = _mainVm.Settings.Hotkey.DisplayText;
            TbStatus.Text = _mainVm.StatusText;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 拦截关闭，改为最小化到托盘
        e.Cancel = true;
        Hide();
    }

    // ===== 按钮事件 =====

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        CaptureRequested?.Invoke();
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (_mainVm == null) return;

        // 应用保存目录
        _mainVm.Settings.SaveDirectory = TbSaveDir.Text.Trim();

        // 重新注册热键
        bool ok = _mainVm.ReregisterHotkey();
        TbStatus.Text = ok ? "设置已应用" : "热键注册失败，请检查是否冲突";
        TbStatus.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("ErrorBrush");
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void BtnChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        // 弹出快捷键录制对话框
        var dialog = new HotkeyEditDialog();
        dialog.Owner = this;
        if (dialog.ShowDialog() == true && _mainVm != null)
        {
            _mainVm.Settings.Hotkey = dialog.ResultConfig;
            UpdateHotkeyDisplay();
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择截图默认保存目录",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TbSaveDir.Text = dialog.SelectedPath;
        }
    }
}
