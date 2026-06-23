using Microsoft.ML.OnnxRuntime;
using Serilog;
using System.IO;

namespace PixSnap.Services;

/// <summary>
/// ONNX InferenceSession 工厂：每次推理创建新会话，用毕即释放显存。
/// 自动探测独立 GPU（NVIDIA / AMD / Intel Arc），失败时回退到 CPU。
/// </summary>
public static class OnnxSessionFactory
{
    private static bool _gpuListLogged;
    private static readonly HashSet<string> CpuOnlyReasonLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>强制 CPU 会话（用于 DirectML 不兼容的模型），首次记录 Information 日志。</summary>
    public static InferenceSession CreateCpuOnlySession(
        string modelPath,
        string reason,
        out string providerName,
        string? providerLabel = null)
    {
        var fileName = Path.GetFileName(modelPath);
        providerName = providerLabel ?? "CPU(模型限制)";

        if (CpuOnlyReasonLogged.Add(fileName))
        {
            Log.Information(
                "ONNX 强制使用 CPU：{Model} — {Reason}",
                fileName,
                reason);
        }
        else
        {
            Log.Debug("ONNX 使用 CPU：{Model}", fileName);
        }

        return CreateCpuSession(modelPath);
    }

    /// <summary>创建新的推理会话：优先尝试 DirectML GPU，失败则回退到 CPU。</summary>
    public static InferenceSession CreateSession(string modelPath, out string providerName)
    {
        LogOnnxRuntimeNativeOnce();

        if (!AiGpuSettings.ShouldUseDirectMl)
        {
            providerName = "CPU(用户选择)";
            return new InferenceSession(modelPath, CreateBaseOptions());
        }

        if (AiGpuSettings.SelectedDeviceId == AiGpuSettings.AutoDeviceId)
            DirectMlDeviceEnumerator.EnsureReadyForInference();

        string? dmlError = null;
        foreach (int dmlDeviceId in AiGpuSettings.GetDirectMlDeviceCandidates())
        {
            var dmlOptions = CreateBaseOptions();
            try
            {
                Log.Debug("尝试 DirectML 设备 {DeviceId}", dmlDeviceId);
                dmlOptions.AppendExecutionProvider_DML(dmlDeviceId);
                providerName = $"DirectML(device={dmlDeviceId})";
                Log.Information("创建 ONNX 会话: {Provider}, 模型 {Model}", providerName, Path.GetFileName(modelPath));
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

    /// <summary>强制使用 CPU 执行提供程序（用于 DML 推理时崩溃的回退）。</summary>
    public static InferenceSession CreateCpuSession(string modelPath)
    {
        return new InferenceSession(modelPath, CreateBaseOptions());
    }

    /// <summary>创建基础 SessionOptions，启用图优化。</summary>
    private static SessionOptions CreateBaseOptions()
    {
        return new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
    }

    private static void LogAvailableGpusOnce()
    {
        if (_gpuListLogged)
            return;

        _gpuListLogged = true;

        var adapters = DxgiAdapterEnumerator.Enumerate()
            .Where(a => !a.IsSoftware)
            .ToList();

        if (adapters.Count == 0)
        {
            Log.Information("未通过 DXGI 检测到硬件显卡，AI 将使用 CPU 或软件适配器");
            return;
        }

        foreach (var adapter in adapters)
        {
            Log.Information(
                "检测到显卡 [DXGI {Index}]: {Name}{Ram}",
                adapter.Index,
                adapter.Name,
                adapter.DedicatedVideoMemory >= 256 * 1024 * 1024
                    ? $", 显存约 {adapter.DedicatedVideoMemory / (1024 * 1024)} MB"
                    : string.Empty);
        }

        Log.Information(
            "DirectML 设备 ID 与 DXGI 适配器索引一致，可在「设置 → AI 加速」中选择");
    }

    private static bool _onnxNativeLogged;

    private static void LogOnnxRuntimeNativeOnce()
    {
        if (_onnxNativeLogged)
            return;

        _onnxNativeLogged = true;
        LogAvailableGpusOnce();
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
