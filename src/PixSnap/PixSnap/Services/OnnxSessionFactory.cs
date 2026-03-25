using Microsoft.ML.OnnxRuntime;
using Serilog;
using System.IO;
using System.Management;

namespace PixSnap.Services;

/// <summary>
/// ONNX InferenceSession 工厂：管理 DirectML / CPU 推理会话的创建与全局缓存。
/// 自动探测独立 GPU（NVIDIA / AMD / Intel Arc），失败时回退到 CPU。
/// </summary>
public static class OnnxSessionFactory
{
    private static readonly Lazy<int> _bestDmlDeviceId = new(DetectBestGpuDeviceId);

    // ── Session 缓存（按模型路径），避免每次推理重新加载 DML 权重 ──────────────
    private static readonly Dictionary<string, (InferenceSession Session, string ProviderName)> _sessionCache = new();
    private static readonly object _cacheLock = new();

    /// <summary>创建新的推理会话：优先尝试 DirectML GPU，失败则回退到 CPU。</summary>
    public static InferenceSession CreateSession(string modelPath, out string providerName)
    {
        string? dmlError = null;
        foreach (int dmlDeviceId in GetDirectMlDeviceCandidates())
        {
            var dmlOptions = CreateBaseOptions();
            try
            {
                Log.Debug("尝试 DirectML 设备 {DeviceId}", dmlDeviceId);
                dmlOptions.AppendExecutionProvider_DML(dmlDeviceId);
                providerName = $"DirectML(device={dmlDeviceId})";
                Log.Information("创建 ONNX 会话成功: {Provider}, 模型 {Model}", providerName, Path.GetFileName(modelPath));
                return new InferenceSession(modelPath, dmlOptions);
            }
            catch (Exception ex)
            {
                Log.Warning("DirectML 设备 {DeviceId} 初始化失败: {Error}", dmlDeviceId, ex.Message);
                dmlError = ex.Message;
            }
        }

        Log.Information("所有 DirectML 尝试失败，回退到 CPU: {Error}", Shorten(dmlError));
        providerName = $"CPU(DML失败: {Shorten(dmlError)})";
        return new InferenceSession(modelPath, CreateBaseOptions());
    }

    /// <summary>强制使用 CPU 执行提供程序（用于 DML 推理时崩溃的回退）。</summary>
    public static InferenceSession CreateCpuSession(string modelPath)
    {
        return new InferenceSession(modelPath, CreateBaseOptions());
    }

    /// <summary>
    /// 获取或创建指定路径的 InferenceSession（带全局缓存）。
    /// 首次调用时初始化；后续直接返回缓存实例，节省 DML 数百毫秒初始化开销。
    /// </summary>
    public static InferenceSession GetOrCreateSession(string modelPath, out string providerName)
    {
        lock (_cacheLock)
        {
            if (_sessionCache.TryGetValue(modelPath, out var cached))
            {
                Log.Debug("ONNX 会话缓存命中: {Model}", Path.GetFileName(modelPath));
                providerName = cached.ProviderName;
                return cached.Session;
            }
            Log.Debug("ONNX 会话缓存未命中，创建新会话: {Model}", Path.GetFileName(modelPath));
            var session = CreateSession(modelPath, out providerName);
            _sessionCache[modelPath] = (session, providerName);
            return session;
        }
    }

    /// <summary>释放所有缓存的 InferenceSession（应在应用退出时调用）。</summary>
    public static void DisposeAll()
    {
        lock (_cacheLock)
        {
            Log.Debug("释放所有缓存的 ONNX 会话: {Count} 个", _sessionCache.Count);
            foreach (var entry in _sessionCache.Values)
                entry.Session.Dispose();
            _sessionCache.Clear();
        }
    }

    /// <summary>创建基础 SessionOptions，启用图优化。</summary>
    private static SessionOptions CreateBaseOptions()
    {
        return new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
    }

    /// <summary>获取 DirectML 设备候选列表：优先使用环境变量 / 独显，然后按序尝试 0–3。</summary>
    private static IEnumerable<int> GetDirectMlDeviceCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("PIXSNAP_DML_DEVICE_ID");
        if (int.TryParse(configured, out var fixedId) && fixedId >= 0)
            return new[] { fixedId };

        int best = _bestDmlDeviceId.Value;
        if (best == 0)
            return new[] { 0, 1, 2, 3 };

        return new[] { best, 0, 1, 2, 3 }.Distinct();
    }

    /// <summary>通过 WMI 查询 Win32_VideoController 找到最适合推理的 GPU 设备索引。</summary>
    private static int DetectBestGpuDeviceId()
    {
        int bestId = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var devices = searcher.Get().Cast<ManagementObject>().ToList();
            try
            {
                for (int index = 0; index < devices.Count; index++)
                {
                    var name = devices[index]["Name"]?.ToString() ?? string.Empty;
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                        || (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("Radeon TM Graphics", StringComparison.OrdinalIgnoreCase))
                        || name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                    {
                        bestId = index;
                        break;
                    }
                }
            }
            finally
            {
                foreach (var device in devices)
                    device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WMI GPU 探测失败，回退到默认设备 0");
        }

        return bestId;
    }

    private const int MaxErrorMessageLength = 64;

    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "未知";

        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= MaxErrorMessageLength ? oneLine : oneLine[..MaxErrorMessageLength] + "...";
    }
}