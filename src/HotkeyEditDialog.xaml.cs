// 编码：UTF-8 BOM
// 快捷键录制对话框后台代码

using PixSnap.Helpers;
using PixSnap.Models;
using System.Windows;
using System.Windows.Input;
// 消除命名空间冲突
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PixSnap;

/// <summary>快捷键录制对话框</summary>
public partial class HotkeyEditDialog : HandyControl.Controls.Window
{
    /// <summary>用户确认的快捷键配置</summary>
    public HotkeyConfig ResultConfig { get; private set; } = new();

    private uint _capturedModifiers;
    private uint _capturedKey;
    private bool _hasCaptured = false;

    public HotkeyEditDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => HotkeyCapture.Focus();
    }

    private void HotkeyCapture_GotFocus(object sender, RoutedEventArgs e)
    {
        HotkeyCapture.Text = "请按下组合键...";
    }

    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // 获取实际按键（排除修饰键本身）
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 如果只按了修饰键，等待后续键
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        // 处理 Escape：清除
        if (key == Key.Escape)
        {
            HotkeyCapture.Text = "点击此处录制";
            _hasCaptured = false;
            BtnOk.IsEnabled = false;
            TbHint.Text = "请包含 Ctrl、Alt、Shift 中的至少一个修饰键";
            return;
        }

        // 构建修饰键掩码
        uint modifiers = 0;
        var modifierKeys = Keyboard.Modifiers;
        string modText = "";

        if (modifierKeys.HasFlag(ModifierKeys.Control))
        {
            modifiers |= NativeMethods.MOD_CONTROL;
            modText += "Ctrl+";
        }
        if (modifierKeys.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= NativeMethods.MOD_ALT;
            modText += "Alt+";
        }
        if (modifierKeys.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= NativeMethods.MOD_SHIFT;
            modText += "Shift+";
        }

        if (modifiers == 0)
        {
            TbHint.Text = "⚠ 全局热键必须包含至少一个修饰键 (Ctrl/Alt/Shift)";
            return;
        }

        // 获取虚拟键码
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        string keyText = key.ToString();

        _capturedModifiers = modifiers;
        _capturedKey = vk;
        _hasCaptured = true;

        HotkeyCapture.Text = modText + keyText;
        TbHint.Text = "✓ 按下确认按钮应用";
        BtnOk.IsEnabled = true;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasCaptured) return;

        ResultConfig = new HotkeyConfig
        {
            Modifiers = _capturedModifiers,
            Key = _capturedKey,
            ModifiersText = BuildModifiersText(_capturedModifiers),
            KeyText = HotkeyCapture.Text.Split('+').Last()
        };

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string BuildModifiersText(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }
}
