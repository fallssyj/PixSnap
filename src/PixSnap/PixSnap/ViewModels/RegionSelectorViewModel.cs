using CommunityToolkit.Mvvm.ComponentModel;
using PixSnap.Models;
using PixSnap.Resources;
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
    private string _selectionText = S.Region_MoveToSelect;

    [ObservableProperty]
    private string _instructionText = S.Region_Instruction;

    public void UpdateHover(WindowInfo? window, ScreenInfo? screen)
    {
        SelectionText = window is not null
            ? string.Format(S.Region_ClickWindow, window.Title)
            : screen is not null
                ? string.Format(S.Region_SpaceScreen, screen.Index + 1)
                : S.Region_MoveToSelect;
    }

    public void UpdateSelection(Rect selection)
    {
        SelectionRect = selection;
        SelectionText = selection.Width > 0 && selection.Height > 0
            ? string.Format(S.Region_AreaInfo, selection.X, selection.Y, selection.Width, selection.Height)
            : S.Region_DragToSelect;
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