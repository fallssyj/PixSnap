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
    private const string KeySaveDirectory = "SaveDirectory";
    private const string KeyAutoSave = "AutoSave";
    private const string KeyRecordingTempDirectory = "RecordingTempDirectory";

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

    // ── 保存目录（JSON 配置文件） ──────────────────────────────────────────────

    /// <summary>读取自定义保存目录，默认为系统图片文件夹。</summary>
    public static string ReadSaveDirectory()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(KeySaveDirectory, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var dir = v.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadSaveDirectory 失败");
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    public static void WriteSaveDirectory(string directory)
    {
        Log.Information("写入保存目录: {Directory}", directory);
        var dict = ReadConfigDict();
        dict[KeySaveDirectory] = directory;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    /// <summary>读取是否启用自动保存。</summary>
    public static bool ReadAutoSave()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return false;
            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(KeyAutoSave, out var v) && v.TryGetInt32(out var b) && b == 1;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadAutoSave 失败");
            return false;
        }
    }

    public static void WriteAutoSave(bool enabled)
    {
        Log.Information("写入自动保存: {Enabled}", enabled);
        var dict = ReadConfigDict();
        dict[KeyAutoSave] = enabled ? 1 : 0;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    // ── 录屏临时目录（JSON 配置文件） ────────────────────────────────────────

    private static readonly string DefaultRecordingTempDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PixSnap");

    /// <summary>读取录屏临时文件存放目录，默认为 文档/PixSnap。</summary>
    public static string ReadRecordingTempDirectory()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(KeyRecordingTempDirectory, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var dir = v.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadRecordingTempDirectory 失败");
        }
        return DefaultRecordingTempDirectory;
    }

    public static void WriteRecordingTempDirectory(string directory)
    {
        Log.Information("写入录屏临时目录: {Directory}", directory);
        var dict = ReadConfigDict();
        dict[KeyRecordingTempDirectory] = directory;
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
                            : kv.Value.ValueKind == JsonValueKind.String
                                ? kv.Value.GetString()!
                                : kv.Value.GetRawText();
                    }
                    return dict;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadConfigDict 失败，配置文件可能已损坏");
            // 备份损坏的配置文件，避免数据永久丢失
            try
            {
                var backupPath = ConfigFilePath + ".corrupted";
                if (File.Exists(ConfigFilePath))
                    File.Copy(ConfigFilePath, backupPath, overwrite: true);
            }
            catch { /* 备份失败不阻塞 */ }
        }
        return [];
    }

    private static void WriteConfigDict(Dictionary<string, object> dict)
    {
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        // 原子写入：先写临时文件，再 rename，避免写入中断导致配置损坏
        var tempPath = ConfigFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigFilePath, overwrite: true);
    }
}
