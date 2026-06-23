using Microsoft.ML.OnnxRuntime;
using Serilog;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace PixSnap.Services;

/// <summary>
/// ONNX 推理辅助：DirectML 失败时回退 CPU，并保证 Session 在 outputs 消费完毕前不被释放。
/// </summary>
internal static class OnnxInferenceHelper
{
    private static readonly object RunLock = new();

    public static SessionRunResult RunWithCpuFallback(
        InferenceSession session,
        string providerName,
        string modelPath,
        IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        lock (RunLock)
        {
            try
            {
                return new SessionRunResult(session.Run(inputs));
            }
            catch (OnnxRuntimeException ex) when (providerName.StartsWith("DirectML", StringComparison.OrdinalIgnoreCase)
                                                   && !IsInputShapeMismatch(ex))
            {
                Log.Warning(ex, "DirectML 推理失败，切换为 CPU 会话: {Model}", Path.GetFileName(modelPath));
                var cpuSession = OnnxSessionFactory.CreateCpuSession(modelPath);
                var outputs = cpuSession.Run(inputs);
                return new SessionRunResult(cpuSession, outputs, adoptReplacement: true);
            }
        }
    }

    private static bool IsInputShapeMismatch(OnnxRuntimeException ex)
        => ex.Message.Contains("invalid dimensions", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("Expected:", StringComparison.OrdinalIgnoreCase);

    internal sealed class SessionRunResult : IEnumerable<DisposableNamedOnnxValue>, IDisposable
    {
        private readonly InferenceSession? _ownedSession;
        private readonly IDisposableReadOnlyCollection<DisposableNamedOnnxValue> _outputs;
        private readonly bool _adoptReplacement;

        public SessionRunResult(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
        {
            _outputs = outputs;
        }

        public SessionRunResult(
            InferenceSession ownedSession,
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
            bool adoptReplacement = false)
        {
            _ownedSession = ownedSession;
            _outputs = outputs;
            _adoptReplacement = adoptReplacement;
        }

        /// <summary>DirectML 回退 CPU 时，调用方应接管此会话并释放原会话。</summary>
        public InferenceSession? ReplacementSession => _adoptReplacement ? _ownedSession : null;

        public IEnumerator<DisposableNamedOnnxValue> GetEnumerator() => _outputs.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int Count => _outputs.Count;

        public void Dispose()
        {
            _outputs.Dispose();
            if (!_adoptReplacement)
                _ownedSession?.Dispose();
        }
    }
}
