using System.Windows;

namespace PixSnap.Services;

/// <summary>为对话框解析合适的 Owner 窗口，避免消息框被其它可见窗口遮挡。</summary>
internal static class WindowOwnerHelper
{
    public static Window? GetActiveOwner(Window? preferred = null)
    {
        if (preferred is { IsVisible: true })
            return preferred;

        var visible = Application.Current.Windows
            .OfType<Window>()
            .Where(w => w.IsVisible)
            .ToList();

        if (visible.Count == 0)
            return Application.Current.MainWindow is { IsVisible: true } main ? main : null;

        return visible.FirstOrDefault(w => w.IsActive)
            ?? visible.OrderByDescending(w => w.Topmost).FirstOrDefault();
    }

    public static void PrepareOwner(Window? owner)
    {
        if (owner is not { IsVisible: true })
            return;

        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;

        owner.Activate();
        owner.Topmost = true;
        owner.Topmost = false;
        owner.Focus();
    }
}
