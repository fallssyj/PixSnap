using Microsoft.Win32;
using PixSnap.Models;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace PixSnap.Services;

/// <summary>
/// 读写应用持久化设置。
/// 开机启动写入系统注册表 Run 键；其余配置存储至 %LocalAppData%\PixSnap\settings.json。
/// </summary>
public static class SettingsService
{
    private const string StartupRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string ConfigFilePath = AppPaths.SettingsFilePath;

    // ── JSON 配置键名 ──────────────────────────────────────────────────────────────
    private const string KeyHotkeyModifiers = "HotkeyModifiers";
    private const string KeyHotkeyKey = "HotkeyKey";
    private const string KeyTheme = "Theme";
    private const string KeyWindowBackdrop = "WindowBackdrop";
    private const string KeyVersion = "Version";
    private const string KeySaveDirectory = "SaveDirectory";
    private const string KeyAutoSave = "AutoSave";
    private const string KeyRecordingTempDirectory = "RecordingTempDirectory";
    private const string KeyAiGpuDeviceId = "AiGpuDeviceId";
    private const string KeyOcrModelTier = "OcrModelTier";
    private const string KeyMattingModel = "MattingModel";
    private const string KeySuperResolutionModel = "SuperResolutionModel";
    private const string KeyTrayDoubleClickAction = "TrayDoubleClickAction";
    private const string KeyLogRetentionDays = "LogRetentionDays";
    private const string KeyUpdateSource = "UpdateSource";
    private const string KeyAutoCheckUpdateOnStartup = "AutoCheckUpdateOnStartup";

    public const int DefaultLogRetentionDays = 7;
    public const int MinLogRetentionDays = 1;
    public const int MaxLogRetentionDays = 365;

    // ── 默认快捷键：Ctrl + Shift + Q ──────────────────────────────────────────
    public static readonly ModifierKeys DefaultHotkeyModifiers = ModifierKeys.Control | ModifierKeys.Shift;
    public static readonly Key DefaultHotkeyKey = Key.Q;
    // settings.json 的 schema 版本号；新增字段时递增，用于未来向后兼容迁移
    private const int CurrentSettingsVersion = 7;
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

    /// <summary>读取窗口背景材质索引，与 <see cref="WindowBackdropHelper.DisplayNames"/> 对应。</summary>
    public static int ReadWindowBackdrop()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return 0;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(KeyWindowBackdrop, out var v)
                && v.TryGetInt32(out var index)
                && index >= 0
                && index < WindowBackdropHelper.DisplayNames.Count
                ? index
                : 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadWindowBackdrop 失败，使用默认值");
            return 0;
        }
    }

    public static void WriteWindowBackdrop(int backdropIndex)
    {
        backdropIndex = Math.Clamp(backdropIndex, 0, WindowBackdropHelper.DisplayNames.Count - 1);
        Log.Information("写入窗口背景: {BackdropIndex}", backdropIndex);
        var dict = ReadConfigDict();
        dict[KeyWindowBackdrop] = backdropIndex;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    // ── 检查更新 ────────────────────────────────────────────────────────────────

    public static UpdateSource ReadUpdateSource() =>
        ReadEnum(KeyUpdateSource, UpdateSource.Gitee);

    public static void WriteUpdateSource(UpdateSource source) =>
        WriteEnum(KeyUpdateSource, source, "检查更新源");

    public static bool ReadAutoCheckUpdateOnStartup() =>
        ReadBool(KeyAutoCheckUpdateOnStartup, fallback: false);

    public static void WriteAutoCheckUpdateOnStartup(bool enabled)
    {
        Log.Information("写入启动时检查更新: {Enabled}", enabled);
        var dict = ReadConfigDict();
        dict[KeyAutoCheckUpdateOnStartup] = enabled ? 1 : 0;
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

    // ── AI GPU（JSON 配置文件） ────────────────────────────────────────────────

    /// <summary>DirectML 设备：-2 自动，-1 仅 CPU，&gt;=0 为设备索引。</summary>
    public static int ReadAiGpuDeviceId()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return AiGpuSettings.AutoDeviceId;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(KeyAiGpuDeviceId, out var v) && v.TryGetInt32(out var id))
                return id >= AiGpuSettings.AutoDeviceId ? id : AiGpuSettings.AutoDeviceId;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadAiGpuDeviceId 失败，使用默认值");
        }

        return AiGpuSettings.AutoDeviceId;
    }

    public static void WriteAiGpuDeviceId(int deviceId)
    {
        Log.Information("写入 AI GPU 设备: {DeviceId}", deviceId);
        var dict = ReadConfigDict();
        dict[KeyAiGpuDeviceId] = deviceId;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    // ── OCR 模型规格（JSON 配置文件） ──────────────────────────────────────────

    public static OcrModelTier ReadOcrModelTier()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return OcrModelTier.Mobile;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(KeyOcrModelTier, out var v) && v.TryGetInt32(out var tier))
                return tier is (int)OcrModelTier.Mobile or (int)OcrModelTier.Server
                    ? (OcrModelTier)tier
                    : OcrModelTier.Mobile;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadOcrModelTier 失败，使用 Mobile");
        }

        return OcrModelTier.Mobile;
    }

    public static void WriteOcrModelTier(OcrModelTier tier)
    {
        Log.Information("写入 OCR 模型规格: {Tier}", tier);
        var dict = ReadConfigDict();
        dict[KeyOcrModelTier] = (int)tier;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    // ── AI 功能模型偏好 ────────────────────────────────────────────────────────

    public static MattingModel ReadMattingModel()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return MattingModel.Rmbg14;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(KeyMattingModel, out var v) && v.TryGetInt32(out var raw))
            {
                // 旧版错误映射曾将 BiRefNet 写成 2
                if (raw == 2)
                    return MattingModel.BiRefNet;

                if (Enum.IsDefined(typeof(MattingModel), raw))
                    return (MattingModel)raw;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadMattingModel 失败，使用默认值");
        }

        return MattingModel.Rmbg14;
    }

    public static void WriteMattingModel(MattingModel model) =>
        WriteEnum(KeyMattingModel, model, "抠图模型");

    public static SuperResolutionModel ReadSuperResolutionModel() =>
        ReadEnum(KeySuperResolutionModel, SuperResolutionModel.X4);

    public static void WriteSuperResolutionModel(SuperResolutionModel model) =>
        WriteEnum(KeySuperResolutionModel, model, "超分模型");

    public static TrayDoubleClickAction ReadTrayDoubleClickAction() =>
        ReadEnum(KeyTrayDoubleClickAction, TrayDoubleClickAction.OpenPreview);

    public static void WriteTrayDoubleClickAction(TrayDoubleClickAction action) =>
        WriteEnum(KeyTrayDoubleClickAction, action, "托盘双击行为");

    public static int ReadLogRetentionDays() => ReadInt(KeyLogRetentionDays, DefaultLogRetentionDays);

    public static void WriteLogRetentionDays(int days)
    {
        days = Math.Clamp(days, MinLogRetentionDays, MaxLogRetentionDays);
        Log.Information("写入日志保留天数: {Days}", days);
        var dict = ReadConfigDict();
        dict[KeyLogRetentionDays] = days;
        dict[KeyVersion] = CurrentSettingsVersion;
        WriteConfigDict(dict);
    }

    private static bool ReadBool(string key, bool fallback)
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return fallback;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var v))
                return fallback;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
                _ => fallback
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadBool({Key}) 失败，使用默认值", key);
            return fallback;
        }
    }

    private static int ReadInt(string key, int fallback)
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return fallback;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.TryGetInt32(out var raw))
                return Math.Clamp(raw, MinLogRetentionDays, MaxLogRetentionDays);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadInt({Key}) 失败，使用默认值", key);
        }

        return fallback;
    }

    private static TEnum ReadEnum<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return fallback;

            var json = File.ReadAllText(ConfigFilePath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(key, out var v) && v.TryGetInt32(out var raw)
                && Enum.IsDefined(typeof(TEnum), raw))
                return (TEnum)(object)raw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ReadEnum({Key}) 失败，使用默认值", key);
        }

        return fallback;
    }

    private static void WriteEnum<TEnum>(string key, TEnum value, string label) where TEnum : struct, Enum
    {
        Log.Information("写入 {Label}: {Value}", label, value);
        var dict = ReadConfigDict();
        dict[key] = Convert.ToInt32(value);
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
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        // 原子写入：先写临时文件，再 rename，避免写入中断导致配置损坏
        var tempPath = ConfigFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigFilePath, overwrite: true);
    }
}
