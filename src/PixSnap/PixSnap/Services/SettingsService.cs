using Microsoft.Win32;
using Serilog;
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

    // ── JSON 配置键名 ──────────────────────────────────────────────────────────────
    private const string KeyHotkeyModifiers = "HotkeyModifiers";
    private const string KeyHotkeyKey = "HotkeyKey";
    private const string KeyTheme = "Theme";
    private const string KeyVersion = "Version";

    // ── 默认快捷键：Ctrl + Shift + Q ──────────────────────────────────────────
    public static readonly ModifierKeys DefaultHotkeyModifiers = ModifierKeys.Control | ModifierKeys.Shift;
    public static readonly Key DefaultHotkeyKey = Key.Q;
    // settings.json 的 schema 版本号；新增字段时递增，用于未来向后兼容迁移
    private const int CurrentSettingsVersion = 1;
    // ── 开机启动 ─────────────────────────────────────────────────────────────

    public static bool ReadStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegPath, writable: false);
        return key?.GetValue("PixSnap") is not null;
    }

    public static void WriteStartupEnabled(bool enabled)
    {
        Log.Information("设置开机启动: {Enabled}", enabled);
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

            var modifiers = root.TryGetProperty(KeyHotkeyModifiers, out var m) && m.TryGetInt32(out var mv)
                ? (ModifierKeys)mv
                : DefaultHotkeyModifiers;
            var key = root.TryGetProperty(KeyHotkeyKey, out var k) && k.TryGetInt32(out var kv)
                ? (Key)kv
                : DefaultHotkeyKey;

            return (modifiers, key);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadHotkey 失败，使用默认值");
            return (DefaultHotkeyModifiers, DefaultHotkeyKey);
        }
    }

    /// <summary>将快捷键写入 settings.json。</summary>
    public static void WriteHotkey(ModifierKeys modifiers, Key key)
    {
        Log.Information("写入快捷键: {Modifiers}+{Key}", modifiers, key);
        // 读取现有内容，合并写入，避免覆盖其他字段
        var dict = ReadConfigDict();
        dict[KeyHotkeyModifiers] = (int)modifiers;
        dict[KeyHotkeyKey] = (int)key;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    // ── 主题（JSON 配置文件） ──────────────────────────────────────────────────

    /// <summary>
    /// 读取主题索引：0 = Auto, 1 = Dark, 2 = Light。
    /// </summary>
    public static int ReadTheme()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return 0;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(KeyTheme, out var v) && v.TryGetInt32(out var t) && t is >= 0 and <= 2
                ? t
                : 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadTheme 失败，使用默认值");
            return 0;
        }
    }

    /// <summary>将主题索引写入 settings.json。</summary>
    public static void WriteTheme(int themeIndex)
    {
        Log.Information("写入主题: {ThemeIndex}", themeIndex);
        var dict = ReadConfigDict();
        dict[KeyTheme] = themeIndex;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    // ── 内部辅助 ──────────────────────────────────────────────────────────────

    private static Dictionary<string, object> ReadConfigDict()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (result is not null)
                {
                    var dict = new Dictionary<string, object>();
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
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadConfigDict 失败，返回空配置");
        }
        return [];
    }

    private static void WriteConfigDict(Dictionary<string, object> dict)
    {
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFilePath, json);
    }
}
