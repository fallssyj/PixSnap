using Microsoft.ML.OnnxRuntime;
using System.Management;

namespace PixSnap.Services;

public static class OnnxSessionFactory
{
    private static int? _bestDmlDeviceId;

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