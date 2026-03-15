// 编码：UTF-8 BOM
// 截图模式和画板工具枚举定义

namespace PixSnap.Models;

/// <summary>截图模式</summary>
public enum CaptureMode
{
    /// <summary>全屏截图</summary>
    Fullscreen,
    /// <summary>矩形区域截图</summary>
    Rectangle,
    /// <summary>窗口截图</summary>
    Window
}

/// <summary>画板绘制工具类型</summary>
public enum DrawToolType
{
    /// <summary>选择/移动</summary>
    Select,
    /// <summary>自由画笔</summary>
    Pen,
    /// <summary>直线</summary>
    Line,
    /// <summary>矩形</summary>
    Rectangle,
    /// <summary>圆形/椭圆</summary>
    Ellipse,
    /// <summary>箭头</summary>
    Arrow,
    /// <summary>文字标注</summary>
    Text,
    /// <summary>马赛克</summary>
    Mosaic,
    /// <summary>橡皮擦</summary>
    Eraser
}

/// <summary>图片保存格式</summary>
public enum ImageFormat
{
    Png,
    Jpg,
    Bmp,
    Webp
}

/// <summary>快捷键配置模型</summary>
public class HotkeyConfig
{
    /// <summary>呼出截图的修饰键（VK 掩码）</summary>
    public uint Modifiers { get; set; } = 0x0003; // Ctrl + Shift

    /// <summary>呼出截图的虚拟键码</summary>
    public uint Key { get; set; } = 0x41; // A

    /// <summary>修饰键显示文本</summary>
    public string ModifiersText { get; set; } = "Ctrl+Shift";

    /// <summary>键显示文本</summary>
    public string KeyText { get; set; } = "A";

    /// <summary>完整快捷键显示</summary>
    public string DisplayText => $"{ModifiersText}+{KeyText}";
}

/// <summary>应用程序设置</summary>
public class AppSettings
{
    /// <summary>全局快捷键配置</summary>
    public HotkeyConfig Hotkey { get; set; } = new();

    /// <summary>默认截图模式</summary>
    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Rectangle;

    /// <summary>默认保存格式</summary>
    public ImageFormat DefaultSaveFormat { get; set; } = ImageFormat.Png;

    /// <summary>默认保存目录（空则使用桌面）</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>截图后自动保存（不弹出处理窗口）</summary>
    public bool AutoSave { get; set; } = false;

    /// <summary>截图后复制到剪贴板</summary>
    public bool CopyToClipboard { get; set; } = true;
}

/// <summary>绘制元素基类</summary>
public abstract class DrawElement
{
    public DrawToolType ToolType { get; set; }
    public System.Windows.Media.Color StrokeColor { get; set; } = System.Windows.Media.Colors.Red;
    public double StrokeWidth { get; set; } = 2.0;
    public bool IsFilled { get; set; } = false;
    public System.Windows.Media.Color FillColor { get; set; } = System.Windows.Media.Colors.Transparent;
}

/// <summary>路径绘制元素（画笔/直线）</summary>
public class PathDrawElement : DrawElement
{
    public System.Collections.Generic.List<System.Windows.Point> Points { get; set; } = new();
}

/// <summary>形状绘制元素（矩形/椭圆）</summary>
public class ShapeDrawElement : DrawElement
{
    public System.Windows.Rect Bounds { get; set; }
}

/// <summary>箭头绘制元素</summary>
public class ArrowDrawElement : DrawElement
{
    public System.Windows.Point Start { get; set; }
    public System.Windows.Point End { get; set; }
}

/// <summary>文字标注元素</summary>
public class TextDrawElement : DrawElement
{
    public System.Windows.Point Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 16.0;
    public System.Windows.Media.Color TextColor { get; set; } = System.Windows.Media.Colors.Red;
}
