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
    private static bool _gpuListLogged;

    // ── Session 缓存（按模型路径），避免每次推理重新加载 DML 权重 ──────────────
    private static readonly Dictionary<string, (InferenceSession Session, string ProviderName, DateTime LastUsed)> _sessionCache = new();
    private static readonly object _cacheLock = new();
    private const int MaxCachedSessions = 4;

    /// <summary>创建新的推理会话：优先尝试 DirectML GPU，失败则回退到 CPU。</summary>
    public static InferenceSession CreateSession(string modelPath, out string providerName)
    {
        LogOnnxRuntimeNativeOnce();

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
        providerName = string.Format("CPU(DML失败: {0})", Shorten(dmlError));
        return new InferenceSession(modelPath, CreateBaseOptions());
    }

    /// <summary>DirectML 推理失败后，将缓存会话替换为 CPU 并返回新会话。</summary>
    public static InferenceSession RecreateSessionAsCpu(string modelPath, out string providerName)
    {
        lock (_cacheLock)
        {
            if (_sessionCache.TryGetValue(modelPath, out var cached)
                && cached.ProviderName.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
            {
                _sessionCache[modelPath] = (cached.Session, cached.ProviderName, DateTime.UtcNow);
                providerName = cached.ProviderName;
                return cached.Session;
            }

            if (_sessionCache.TryGetValue(modelPath, out cached))
            {
                Log.Information("释放 DirectML 会话并切换 CPU: {Model}", Path.GetFileName(modelPath));
                cached.Session.Dispose();
                _sessionCache.Remove(modelPath);
            }

            providerName = "CPU(DirectML推理失败回退)";
            var session = CreateCpuSession(modelPath);
            _sessionCache[modelPath] = (session, providerName, DateTime.UtcNow);
            return session;
        }
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
                _sessionCache[modelPath] = (cached.Session, cached.ProviderName, DateTime.UtcNow);
                providerName = cached.ProviderName;
                return cached.Session;
            }

            // 缓存已满时驱逐最久未使用的会话
            if (_sessionCache.Count >= MaxCachedSessions)
            {
                var oldest = _sessionCache.MinBy(kv => kv.Value.LastUsed);
                Log.Information("ONNX 缓存已满，驱逐最久未使用: {Model}", Path.GetFileName(oldest.Key));
                oldest.Value.Session.Dispose();
                _sessionCache.Remove(oldest.Key);
            }

            Log.Debug("ONNX 会话缓存未命中，创建新会话: {Model}", Path.GetFileName(modelPath));
            var session = CreateSession(modelPath, out providerName);
            _sessionCache[modelPath] = (session, providerName, DateTime.UtcNow);
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

    /// <summary>获取 DirectML 设备候选列表。DirectML 设备 ID 对应 DXGI 枚举顺序，与 WMI 索引无关。</summary>
    private static IEnumerable<int> GetDirectMlDeviceCandidates()
    {
        LogAvailableGpusOnce();

        var configured = Environment.GetEnvironmentVariable("PIXSNAP_DML_DEVICE_ID");
        if (int.TryParse(configured, out var fixedId) && fixedId >= 0)
        {
            Log.Information("使用环境变量指定的 DirectML 设备: {DeviceId}", fixedId);
            return new[] { fixedId };
        }

        // 依次尝试 0–3，首个能成功创建 Session 的设备即为实际 GPU
        return new[] { 0, 1, 2, 3 };
    }

    private static void LogAvailableGpusOnce()
    {
        if (_gpuListLogged)
            return;

        _gpuListLogged = true;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            var devices = searcher.Get().Cast<ManagementObject>().ToList();
            try
            {
                for (int index = 0; index < devices.Count; index++)
                {
                    var name = devices[index]["Name"]?.ToString() ?? "未知";
                    var ramObj = devices[index]["AdapterRAM"];
                    var ramMb = ramObj is uint ram && ram != uint.MaxValue ? ram / (1024 * 1024) : (uint?)null;
                    Log.Information(
                        "检测到显卡 [{Index}]: {Name}{Ram}",
                        index,
                        name,
                        ramMb.HasValue ? $", 显存约 {ramMb} MB" : string.Empty);
                }

                Log.Information(
                    "DirectML 将按 device 0→3 依次尝试；WMI 序号与 DirectML 设备 ID 不一定一致。独显未命中时可设 PIXSNAP_DML_DEVICE_ID=0/1/2/3");
            }
            finally
            {
                foreach (var device in devices)
                    device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WMI GPU 枚举失败");
        }
    }

    private static bool _onnxNativeLogged;

    private static void LogOnnxRuntimeNativeOnce()
    {
        if (_onnxNativeLogged)
            return;

        _onnxNativeLogged = true;
        var baseDir = AppContext.BaseDirectory;
        var ortPath = Path.Combine(baseDir, "onnxruntime.dll");
        var dmlPath = Path.Combine(baseDir, "DirectML.dll");
        if (File.Exists(ortPath))
        {
            var sizeMb = new FileInfo(ortPath).Length / (1024.0 * 1024.0);
            Log.Information("onnxruntime.dll: {Path} ({Size:F1} MB), DirectML.dll 存在: {HasDml}",
                ortPath, sizeMb, File.Exists(dmlPath));
            if (!File.Exists(dmlPath))
                Log.Warning("输出目录缺少 DirectML.dll，AppendExecutionProvider_DML 将失败。请 Rebuild Solution");
        }
        else
        {
            Log.Warning("未找到 onnxruntime.dll: {Path}", ortPath);
        }
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