// 编码：UTF-8 BOM
// 应用程序入口，管理托盘图标和全局协调

using PixSnap.View;
using PixSnap.ViewModels;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace PixSnap;

/// <summary>应用程序类，管理托盘图标和窗口生命周期</summary>
public partial class App : System.Windows.Application
{
    private MainViewModel? _mainVm;
    private MainWindow? _mainWindow;
    private NotifyIcon? _notifyIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 设置关闭模式：仅在显式调用 Shutdown() 时才退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 初始化主 ViewModel
        _mainVm = new MainViewModel();
        _mainVm.ShowCaptureRequested += ShowCaptureToolbar;
        _mainVm.ShowEditorRequested += ShowEditor;
        _mainVm.ShowMainWindowRequested += ShowMainWindow;

        // 初始化主窗口（设置窗口，默认隐藏）
        _mainWindow = new MainWindow();
        _mainWindow.SetViewModel(_mainVm);
        _mainWindow.CaptureRequested += ShowCaptureToolbar;

        // 初始化系统托盘图标
        InitTrayIcon();

        // 显示 Growl 容器（绑定到主窗口）
        HandyControl.Controls.Growl.SetGrowlParent(_mainWindow, true);
    }

    /// <summary>初始化系统托盘图标和右键菜单</summary>
    private void InitTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "PixSnap 截图工具",
            Visible = true,
            Icon = CreateTrayIcon()
        };

        // 右键菜单
        var menu = new ContextMenuStrip();
        menu.Renderer = new DarkMenuRenderer(); // 深色风格

        var itemCapture = new ToolStripMenuItem("截图 (Ctrl+Shift+A)");
        itemCapture.Click += (_, _) => ShowCaptureToolbar();

        var itemSettings = new ToolStripMenuItem("设置");
        itemSettings.Click += (_, _) => ShowMainWindow();

        var itemSeparator = new ToolStripSeparator();

        var itemExit = new ToolStripMenuItem("退出");
        itemExit.Click += (_, _) => ExitApp();

        menu.Items.AddRange([itemCapture, itemSettings, itemSeparator, itemExit]);
        _notifyIcon.ContextMenuStrip = menu;

        // 双击托盘图标显示设置窗口
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>使用 GDI 创建托盘图标（相机样式）</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        // 相机外框
        using var bodyBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 91, 141, 239));
        g.FillRoundedRectangle(bodyBrush, new RectangleF(2, 8, 28, 20), 4);

        // 镜头
        using var lensBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 30, 30, 46));
        g.FillEllipse(lensBrush, 10, 11, 14, 14);

        // 镜头高光
        using var innerBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 91, 141, 239));
        g.FillEllipse(innerBrush, 12, 13, 10, 10);

        // 闪光灯
        g.FillRectangle(bodyBrush, 6, 5, 5, 4);

        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>显示截图工具栏</summary>
    private void ShowCaptureToolbar()
    {
        Dispatcher.Invoke(() =>
        {
            _mainWindow?.Hide(); // 隐藏设置窗口避免干扰

            var toolbar = new CaptureToolbarWindow();
            toolbar.CaptureCompleted += (bitmap) =>
            {
                ShowEditor(bitmap);
            };
            toolbar.Show();
        });
    }

    /// <summary>显示图片编辑器</summary>
    private void ShowEditor(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        Dispatcher.Invoke(() =>
        {
            var editor = new ImageEditorWindow();
            editor.LoadBitmap(bitmap);
            editor.Show();
            editor.Activate();
        });
    }

    /// <summary>显示主设置窗口</summary>
    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.Activate();
                _mainWindow.WindowState = WindowState.Normal;
            }
        });
    }

    /// <summary>退出程序</summary>
    private void ExitApp()
    {
        _notifyIcon?.Dispose();
        _mainVm?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _mainVm?.Dispose();
        base.OnExit(e);
    }
}

// ===== 辅助扩展 =====

/// <summary>GDI 圆角矩形扩展</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

/// <summary>深色风格右键菜单渲染器</summary>
internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected)
        {
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(255, 91, 141, 239));
            e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
        }
        else
        {
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(255, 40, 40, 56));
            e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
        }
    }
}

/// <summary>深色菜单颜色表</summary>
internal class DarkColorTable : ProfessionalColorTable
{
    public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(255, 91, 141, 239);
    public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(255, 64, 64, 88);
    public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(255, 91, 141, 239);
    public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(255, 40, 40, 56);
    public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(255, 30, 30, 46);
    public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(255, 30, 30, 46);
    public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(255, 30, 30, 46);
}

