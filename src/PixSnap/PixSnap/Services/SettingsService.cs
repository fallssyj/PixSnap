using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace PixSnap.Services;

/// <summary>
/// 读写应用持久化设置。
/// 开机启动写入系统注册表 Run 键；热键配置存储至程序目录下的 settings.json。
/// </summary>
public static class SettingsService
{
    private const string StartupRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    // 配置文件路径：与可执行文件同目录
    private static readonly string ConfigFilePath =
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    // ── 默认快捷键：Ctrl + Shift + Q ──────────────────────────────────────────
    public static readonly ModifierKeys DefaultHotkeyModifiers = ModifierKeys.Control | ModifierKeys.Shift;
    public static readonly Key DefaultHotkeyKey = Key.Q;

    // ── 开机启动 ─────────────────────────────────────────────────────────────

    public static bool ReadStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, writable: false);
        return key?.GetValue("PixSnap") is not null;
    }

    public static void WriteStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, writable: true);
        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
                key?.SetValue("PixSnap", $"\"{exePath}\"");
        }
        else
        {
            key?.DeleteValue("PixSnap", throwOnMissingValue: false);
        }
    }

    // ── 截图快捷键（JSON 配置文件） ───────────────────────────────────────────

    /// <summary>从 settings.json 读取快捷键，文件不存在或解析失败时返回默认值。</summary>
    public static (ModifierKeys modifiers, Key key) ReadHotkey()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return (DefaultHotkeyModifiers, DefaultHotkeyKey);

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modifiers = root.TryGetProperty("HotkeyModifiers", out var m) && m.TryGetInt32(out var mv)
                ? (ModifierKeys)mv
                : DefaultHotkeyModifiers;
            var key = root.TryGetProperty("HotkeyKey", out var k) && k.TryGetInt32(out var kv)
                ? (Key)kv
                : DefaultHotkeyKey;

            return (modifiers, key);
        }
        catch
        {
            return (DefaultHotkeyModifiers, DefaultHotkeyKey);
        }
    }

    /// <summary>将快捷键写入 settings.json。</summary>
    public static void WriteHotkey(ModifierKeys modifiers, Key key)
    {
        // 读取现有内容，合并写入，避免覆盖其他字段
        var dict = ReadConfigDict();
        dict["HotkeyModifiers"] = (int)modifiers;
        dict["HotkeyKey"] = (int)key;
        WriteConfigDict(dict);
    }

    // ── 内部辅助 ──────────────────────────────────────────────────────────────

    private static System.Collections.Generic.Dictionary<string, object> ReadConfigDict()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var result = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(json);
                if (result is not null)
                {
                    var dict = new System.Collections.Generic.Dictionary<string, object>();
                    foreach (var kv in result)
                    {
                        dict[kv.Key] = kv.Value.ValueKind == JsonValueKind.Number
                            ? (object)kv.Value.GetInt32()
                            : kv.Value.GetRawText();
                    }
                    return dict;
                }
            }
        }
        catch { }
        return [];
    }

    private static void WriteConfigDict(System.Collections.Generic.Dictionary<string, object> dict)
    {
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFilePath, json);
    }
}
