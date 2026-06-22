using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;

namespace RapidOCRLib;

/// <summary>
/// 由宿主应用（PixSnap）配置 DirectML 设备；OCR 子模型统一经此创建 InferenceSession。
/// </summary>
public static class OnnxSessionHelper
{
    /// <summary>null = 自动尝试 device 0–3；-1 = 仅 CPU；&gt;= 0 为指定 DirectML 设备。</summary>
    public static int? DirectMlDeviceId { get; set; }

    public static bool PreferDirectMl { get; set; } = true;

    /// <summary>自动模式下设备尝试顺序（由宿主注入，例如独显优先）。</summary>
    public static Func<IEnumerable<int>>? AutoDeviceOrderProvider { get; set; }

    public static InferenceSession Create(string modelPath, int numThread)
    {
        if (PreferDirectMl)
        {
            foreach (int deviceId in GetDeviceCandidates())
            {
                try
                {
                    var options = CreateBaseOptions(numThread);
                    options.AppendExecutionProvider_DML(deviceId);
                    return new InferenceSession(modelPath, options);
                }
                catch
                {
                    // 尝试下一个设备
                }
            }
        }

        return new InferenceSession(modelPath, CreateBaseOptions(numThread));
    }

    private static IEnumerable<int> GetDeviceCandidates()
    {
        if (DirectMlDeviceId == -1)
            yield break;

        if (DirectMlDeviceId is >= 0)
        {
            yield return DirectMlDeviceId.Value;
            yield break;
        }

        foreach (int deviceId in GetAutoDeviceOrder())
            yield return deviceId;
    }

    private static IEnumerable<int> GetAutoDeviceOrder()
    {
        if (AutoDeviceOrderProvider != null)
        {
            foreach (int deviceId in AutoDeviceOrderProvider())
                yield return deviceId;
            yield break;
        }

        yield return 0;
        yield return 1;
        yield return 2;
        yield return 3;
    }

    private static SessionOptions CreateBaseOptions(int numThread)
    {
        return new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
            InterOpNumThreads = numThread,
            IntraOpNumThreads = numThread
        };
    }
}
