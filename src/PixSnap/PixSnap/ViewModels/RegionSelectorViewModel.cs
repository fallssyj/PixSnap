using CommunityToolkit.Mvvm.ComponentModel;
using PixSnap.Models;
using System.Windows;

namespace PixSnap.ViewModels;

// ViewModel 只维护可绑定的选择语义和提示文本，不参与 Canvas 坐标或控件可视化细节。
public partial class RegionSelectorViewModel : ObservableObject
{
    [ObservableProperty]
    private Rect _selectionRect;

    [ObservableProperty]
    private Rect _highlightRect;

    [ObservableProperty]
    private string _selectionText = "移动鼠标以选择截图对象";

    [ObservableProperty]
    private string _instructionText = "悬停窗口后左键单击截窗口，按住左键拖动截矩形，Space 截当前显示器，Esc 或右键退出";

    public void UpdateHover(WindowInfo? window, ScreenInfo? screen)
    {
        SelectionText = window is not null
            ? string.Format("单击截取窗口: {0}", window.Title)
            : screen is not null
                ? string.Format("按 Space 截取显示器 {0}", screen.Index + 1)
                : "移动鼠标以选择截图对象";
    }

    public void UpdateSelection(Rect selection)
    {
        SelectionRect = selection;
        SelectionText = selection.Width > 0 && selection.Height > 0
            ? string.Format("区域 X:{0:0} Y:{1:0} W:{2:0} H:{3:0}", selection.X, selection.Y, selection.Width, selection.Height)
            : "拖动以选择截图区域";
    }

    // 高亮窗口的语义状态保留在 ViewModel 中，具体怎么画由 View 决定。
    public void UpdateWindowHighlight(Rect highlight)
    {
        HighlightRect = highlight;
    }

    public void ClearSelection()
    {
        SelectionRect = Rect.Empty;
    }

    public void ClearHighlight()
    {
        HighlightRect = Rect.Empty;
    }
}