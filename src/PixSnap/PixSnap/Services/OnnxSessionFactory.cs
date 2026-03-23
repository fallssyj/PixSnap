using Microsoft.ML.OnnxRuntime;
using System.Management;

namespace PixSnap.Services;

public static class OnnxSessionFactory
{
    private static int? _bestDmlDeviceId;

    // ── Session 缓存（按模型路径），避免每次推理重新加载 DML 权重 ──────────────
    private static readonly Dictionary<string, (InferenceSession Session, string ProviderName)> _sessionCache = new();
    private static readonly object _cacheLock = new();

    public static InferenceSession CreateSession(string modelPath, out string providerName)
    {
        string? dmlError = null;
        foreach (int dmlDeviceId in GetDirectMlDeviceCandidates())
        {
            var dmlOptions = CreateBaseOptions();
            try
            {
                dmlOptions.AppendExecutionProvider_DML(dmlDeviceId);
                providerName = $"DirectML(device={dmlDeviceId})";
                return new InferenceSession(modelPath, dmlOptions);
            }
            catch (Exception ex)
            {
                dmlError = ex.Message;
            }
        }

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
                providerName = cached.ProviderName;
                return cached.Session;
            }
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
            foreach (var entry in _sessionCache.Values)
                entry.Session.Dispose();
            _sessionCache.Clear();
        }
    }

    private static SessionOptions CreateBaseOptions()
    {
        return new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
    }

    private static IEnumerable<int> GetDirectMlDeviceCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("PIXSNAP_DML_DEVICE_ID");
        if (int.TryParse(configured, out var fixedId) && fixedId >= 0)
            return new[] { fixedId };

        int best = GetBestGpuDeviceId();
        if (best == 0)
            return new[] { 0, 1, 2, 3 };

        return new[] { best, 0, 1, 2, 3 }.Distinct();
    }

    private static int GetBestGpuDeviceId()
    {
        if (_bestDmlDeviceId.HasValue)
            return _bestDmlDeviceId.Value;

        int bestId = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var devices = searcher.Get().Cast<ManagementObject>().ToList();

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
        catch
        {
            bestId = 0;
        }

        _bestDmlDeviceId = bestId;
        return bestId;
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "未知";

        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 64 ? oneLine : oneLine[..64] + "...";
    }
}