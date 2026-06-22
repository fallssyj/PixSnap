using Microsoft.ML.OnnxRuntime;
using PixSnap.Models;
using Serilog;
using System.IO;

namespace PixSnap.Services;

/// <summary>枚举可用于 DirectML 的 GPU 设备（供设置页下拉框）。</summary>
public static class DirectMlDeviceEnumerator
{
    private static readonly string ProbeModelPath = Path.Combine(
        AppContext.BaseDirectory, "onnx", "ocr", "ch_ppocr_mobile_v2.0_cls_infer.onnx");

    private static readonly object CacheLock = new();
    private static IReadOnlyList<GpuDeviceOption>? _cachedOptions;
    private static IReadOnlyList<int>? _workingDeviceIds;
    private static Task<IReadOnlyList<GpuDeviceOption>>? _enumerateTask;

    public static void WarmCache() => _ = EnsureEnumeratedAsync();

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedOptions = null;
            _workingDeviceIds = null;
            _enumerateTask = null;
        }
    }

    public static IReadOnlyList<GpuDeviceOption> GetCachedOrDefault()
    {
        lock (CacheLock)
            return _cachedOptions ?? CreateDefaultOptions();
    }

    public static Task<IReadOnlyList<GpuDeviceOption>> EnsureEnumeratedAsync()
    {
        lock (CacheLock)
        {
            if (_cachedOptions != null)
                return Task.FromResult(_cachedOptions);

            _enumerateTask ??= Task.Run(EnumerateCore);
            return _enumerateTask;
        }
    }

    /// <summary>在创建 ONNX 会话前确保已完成设备探测（自动模式避免误用设备 0）。</summary>
    public static void EnsureReadyForInference()
    {
        lock (CacheLock)
        {
            if (_cachedOptions != null)
                return;

            if (_enumerateTask is not null)
            {
                try
                {
                    _enumerateTask.Wait(TimeSpan.FromSeconds(45));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "等待 DirectML 设备探测超时");
                }
            }

            if (_cachedOptions is null)
                EnumerateCore();
        }
    }

    /// <summary>自动模式下优先尝试的设备顺序（独显优先）。</summary>
    public static IReadOnlyList<int> GetPreferredAutoDeviceOrder()
    {
        var allAdapters = DxgiAdapterEnumerator.Enumerate()
            .Where(a => !a.IsSoftware)
            .ToList();

        if (allAdapters.Count == 0)
            return [0, 1, 2, 3];

        lock (CacheLock)
        {
            IEnumerable<int> deviceIds = _workingDeviceIds is { Count: > 0 } working
                ? working
                : allAdapters.Select(a => a.Index);

            var ordered = deviceIds
                .Distinct()
                .OrderByDescending(id => DxgiAdapterEnumerator.ScoreForAutoSelect(allAdapters, id))
                .ThenBy(id => id)
                .ToList();

            Log.Debug("自动 GPU 尝试顺序: {Order}", string.Join(", ", ordered));
            return ordered;
        }
    }

    private static IReadOnlyList<GpuDeviceOption> CreateDefaultOptions() =>
    [
        new(AiGpuSettings.AutoDeviceId, "自动（优先独显）"),
        new(AiGpuSettings.CpuOnlyDeviceId, "仅 CPU")
    ];

    private static IReadOnlyList<GpuDeviceOption> EnumerateCore()
    {
        var options = new List<GpuDeviceOption>
        {
            new(AiGpuSettings.AutoDeviceId, "自动（优先独显）"),
            new(AiGpuSettings.CpuOnlyDeviceId, "仅 CPU")
        };

        var adapters = DxgiAdapterEnumerator.EnumerateUniqueHardware();
        var workingIds = new List<int>();

        foreach (var adapter in adapters)
        {
            if (!TryProbeDirectMlDevice(adapter.Index))
                continue;

            workingIds.Add(adapter.Index);
            var label = DxgiAdapterEnumerator.FormatDisplayName(adapter);
            options.Add(new GpuDeviceOption(adapter.Index, label));
            Log.Information("DirectML 可用设备 {DeviceId}: {Name}", adapter.Index, label);
        }

        if (workingIds.Count == 0)
            Log.Warning("未探测到可用 DirectML GPU，AI 将回退 CPU（可在设置中强制选择 CPU）");

        lock (CacheLock)
        {
            _cachedOptions = options;
            _workingDeviceIds = workingIds;
            _enumerateTask = null;
        }

        return options;
    }

    private static bool TryProbeDirectMlDevice(int deviceId)
    {
        if (!File.Exists(ProbeModelPath))
            return false;

        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_DML(deviceId);
            using var session = new InferenceSession(ProbeModelPath, options);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DirectML 设备 {DeviceId} 探测失败", deviceId);
            return false;
        }
    }
}
