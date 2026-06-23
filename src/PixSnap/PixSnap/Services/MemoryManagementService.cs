using System;
using System.Runtime.InteropServices;

namespace PixSnap.Services;

/// <summary>
/// 常驻托盘应用的内存整理。显存由 ONNX Session / OCR 引擎 Dispose 释放；此处回收托管堆与进程工作集。
/// </summary>
public static partial class MemoryManagementService
{
    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// AI 推理（OCR / 抠图 / 超分 / 擦除）结束后调用，协助回收托管包装与 CPU 工作集。
    /// </summary>
    public static void TrimAfterHeavyWork() => TrimManagedMemory(aggressive: true);

    /// <summary>预览/录屏窗口关闭、大图引用解除后调用。</summary>
    public static void TrimAfterUiRelease() => TrimManagedMemory(aggressive: false);

    private static void TrimManagedMemory(bool aggressive)
    {
        if (aggressive)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        EmptyWorkingSet(GetCurrentProcess());
    }
}
