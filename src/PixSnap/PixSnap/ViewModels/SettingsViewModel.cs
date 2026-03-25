using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PixSnap.Resources;
using PixSnap.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace PixSnap.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    // 当前已确认的快捷键（初始化时从注册表读取，Save 时写入）
    private int _pendingModifiers;
    private int _pendingKey;

    [ObservableProperty]
    private bool _isStartupEnabled;

    /// <summary>主题索引：0 = 自动, 1 = 深色, 2 = 浅色。</summary>
    [ObservableProperty]
    private int _selectedThemeIndex;

    /// <summary>是否处于快捷键录制状态（用户点击输入框后激活）。</summary>
    [ObservableProperty]
    private bool _isRecordingHotkey;

    /// <summary>快捷键显示文本，录制中时显示提示语。</summary>
    [ObservableProperty]
    private string _hotkeyDisplayText = string.Empty;

    /// <summary>快捷键已保存时触发，携带新的修饰键和按键。</summary>
    public event Action<ModifierKeys, Key>? HotkeyChanged;

    /// <summary>主题已保存时触发，携带新的主题索引。</summary>
    public event Action<int>? ThemeChanged;

    /// <summary>请求关闭设置窗口。</summary>
    public event Action? RequestClose;

    public SettingsViewModel()
    {
        _isStartupEnabled = SettingsService.ReadStartupEnabled();
        _selectedThemeIndex = SettingsService.ReadTheme();
        var (modifiers, key) = SettingsService.ReadHotkey();
        _pendingModifiers = (int)modifiers;
        _pendingKey = (int)key;
        UpdateHotkeyDisplay();
    }

    // 进入/退出录制状态时同步显示文本
    partial void OnIsRecordingHotkeyChanged(bool value) =>
        HotkeyDisplayText = value ? S.Settings_PressHotkey : BuildDisplayText(_pendingModifiers, _pendingKey);

    /// <summary>
    /// 由 View 在捕获到合法按键时调用，立即更新显示并存储新的快捷键组合。
    /// </summary>
    public void RecordKey(Key key, ModifierKeys modifiers)
    {
        _pendingModifiers = (int)modifiers;
        _pendingKey = (int)key;
        // 立即刷新显示文本，不等待失焦事件
        UpdateHotkeyDisplay();
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        _pendingModifiers = 0;
        _pendingKey = (int)Key.None;
        UpdateHotkeyDisplay();
    }

    [RelayCommand]
    private void Save()
    {
        SettingsService.WriteStartupEnabled(IsStartupEnabled);
        SettingsService.WriteHotkey((ModifierKeys)_pendingModifiers, (Key)_pendingKey);
        SettingsService.WriteTheme(SelectedThemeIndex);
        Log.Information("设置已保存: 开机启动={Startup}, 快捷键={Modifiers}+{Key}, 主题={Theme}", IsStartupEnabled, (ModifierKeys)_pendingModifiers, (Key)_pendingKey, SelectedThemeIndex);
        HotkeyChanged?.Invoke((ModifierKeys)_pendingModifiers, (Key)_pendingKey);
        ThemeChanged?.Invoke(SelectedThemeIndex);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    private void UpdateHotkeyDisplay() =>
        HotkeyDisplayText = BuildDisplayText(_pendingModifiers, _pendingKey);

    private static string BuildDisplayText(int modifiers, int key)
    {
        var k = (Key)key;
        if (k == Key.None) return S.Settings_None;

        var parts = new List<string>(5);
        var mods = (ModifierKeys)modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToLabel(k));
        return string.Join(" + ", parts);
    }

    // 将少数特殊键映射为更易读的标签，其余直接用 Key.ToString()
    private static string KeyToLabel(Key key) => key switch
    {
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemBackslash => "\\",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.Space => "Space",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        Key.Prior => "PgUp",
        Key.Next => "PgDn",
        _ => key.ToString()
    };
}
