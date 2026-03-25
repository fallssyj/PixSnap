using PixSnap.ViewModels;
using MicaWPF.Controls;
using System.Windows;
using System.Windows.Input;

namespace PixSnap.Views;

public partial class SettingsWindow : MicaWindow
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow()
    {
        ViewModel = new SettingsViewModel();
        ViewModel.RequestClose += Close;

        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    // ── 快捷键输入框 ──────────────────────────────────────────────────────────

    private void HotkeyInputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // 清除选中文字（避免显示文本被框选），然后告知 ViewModel 进入录制状态
        HotkeyInputBox.SelectionLength = 0;
        ViewModel.IsRecordingHotkey = true;
    }

    private void HotkeyInputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 失焦时退出录制状态，ViewModel 的 OnIsRecordingHotkeyChanged 会刷新显示文本
        ViewModel.IsRecordingHotkey = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= Close;
        base.OnClosed(e);
    }

    private void HotkeyInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ViewModel.IsRecordingHotkey) return;

        // Alt 键组合时 e.Key 为 System，需取 e.SystemKey
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 忽略单独按下修饰键（等用户继续按非修饰键）
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return;

        // Esc：取消录制，不修改快捷键
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        // 将新的组合键写入 ViewModel，然后通过失焦触发状态退出和显示刷新
        ViewModel.RecordKey(key, Keyboard.Modifiers);
        Keyboard.ClearFocus();
        e.Handled = true;
    }
}
