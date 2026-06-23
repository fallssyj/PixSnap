using Serilog;

namespace PixSnap.Services;

/// <summary>
/// 常驻进程中的 AI 推理资源收尾：ONNX/OCR 会话由各自服务 Dispose，此处回收托管堆与工作集。
/// </summary>
internal static class InferenceResourceHelper
{
    internal static void OnInferenceCompleted()
    {
        try
        {
            MemoryManagementService.TrimAfterHeavyWork();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "推理后内存整理失败");
        }
    }
}
