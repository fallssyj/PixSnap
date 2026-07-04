namespace PixSnap.Services;

using System.IO;

/// <summary>
/// 应用路径：安装目录只读；日志、设置、下载的模型等写入用户 LocalAppData。
/// </summary>
public static class AppPaths
{
    public static string InstallDirectory => AppContext.BaseDirectory;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixSnap");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    public static string ModelsRootDirectory => Path.Combine(DataDirectory, "onnx");

    public static string GetInstallPath(string relativePath) =>
        Path.Combine(InstallDirectory, relativePath);

    public static string GetDataPath(string relativePath) =>
        Path.Combine(DataDirectory, relativePath);

    /// <summary>启动时创建数据目录，并将旧版安装目录下的 settings.json 迁移到用户目录。</summary>
    public static void Initialize()
    {
        EnsureDataDirectories();
        MigrateLegacySettings();
    }

    public static void EnsureDataDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ModelsRootDirectory);
        Directory.CreateDirectory(Path.Combine(ModelsRootDirectory, "ocr"));
    }

    public static void MigrateLegacySettings() =>
        MigrateLegacyFile(GetInstallPath("settings.json"), SettingsFilePath);

    private static void MigrateLegacyFile(string legacyPath, string newPath)
    {
        if (!File.Exists(legacyPath) || File.Exists(newPath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.Copy(legacyPath, newPath);
        }
        catch
        {
            // 安装目录不可写时忽略；新路径会在保存设置时创建。
        }
    }
}
