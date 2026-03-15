// 编码：UTF-8 BOM
// 截图工具栏窗口后台代码

using PixSnap.Models;
using PixSnap.ViewModels;
using System.Windows;
using System.Windows.Input;
// 消除命名空间冲突
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using CaptureMode = PixSnap.Models.CaptureMode;

namespace PixSnap.View;

/// <summary>截图工具栏浮动窗口</summary>
public partial class CaptureToolbarWindow : Window
{
    private readonly CaptureToolbarViewModel _vm;

    /// <summary>截图完成事件（传出截图结果）</summary>
    public event Action<System.Windows.Media.Imaging.BitmapSource>? CaptureCompleted;

    public CaptureToolbarWindow()
    {
        InitializeComponent();
        _vm = new CaptureToolbarViewModel();
        DataContext = _vm;

        // 绑定事件
        _vm.CancelRequested += OnCancelled;
        _vm.OnStartCaptureRequested += OnStartCapture;
        _vm.CaptureCompleted += OnCaptureCompleted;

        // 将工具栏定位在屏幕中上方
        PositionWindow();
    }

    /// <summary>定位工具栏到屏幕中央顶部</summary>
    private void PositionWindow()
    {
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = screen.Top + 60;
    }

    /// <summary>允许拖动工具栏</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
    }

    /// <summary>按 Esc 取消</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnCancelled()
    {
        Close();
    }

    private void OnStartCapture(CaptureMode mode)
    {
        // 隐藏工具栏，避免被截入画面
        Hide();

        // 延迟一帧确保工具栏完全隐藏
        Dispatcher.InvokeAsync(() =>
        {
            System.Threading.Thread.Sleep(100);

            switch (mode)
            {
                case CaptureMode.Fullscreen:
                    _vm.DoFullscreenCapture();
                    Close();
                    break;

                case CaptureMode.Rectangle:
                    OpenRegionSelector();
                    break;

                case CaptureMode.Window:
                    OpenWindowSelector();
                    break;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenRegionSelector()
    {
        var selector = new RegionSelectorWindow();
        selector.RegionSelected += (x, y, w, h) =>
        {
            _vm.DoRegionCapture(x, y, w, h);
            Close();
        };
        selector.Cancelled += () => Close();
        selector.ShowDialog();
    }

    private void OpenWindowSelector()
    {
        var selector = new WindowSelectorWindow();
        selector.WindowSelected += (hWnd) =>
        {
            _vm.DoWindowCapture(hWnd);
            Close();
        };
        selector.Cancelled += () => Close();
        selector.ShowDialog();
    }

    private void OnCaptureCompleted(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        CaptureCompleted?.Invoke(bitmap);
    }
}
