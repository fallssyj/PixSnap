using PixSnap.Models;
using Serilog;
using Vortice.DXGI;

namespace PixSnap.Services;

/// <summary>通过 DXGI 枚举显卡；DirectML device_id 与 DXGI Adapter 索引一致。</summary>
internal static class DxgiAdapterEnumerator
{
    private const uint VendorNvidia = 0x10DE;
    private const uint VendorAmd = 0x1002;
    private const uint VendorIntel = 0x8086;

    public sealed record DxgiAdapterInfo(
        int Index,
        string Name,
        ulong DedicatedVideoMemory,
        bool IsSoftware,
        uint VendorId,
        uint DeviceId);

    public static IReadOnlyList<DxgiAdapterInfo> Enumerate()
    {
        var adapters = new List<DxgiAdapterInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out var adapter).Failure)
                    break;

                using (adapter)
                {
                    var desc = adapter.Description1;
                    adapters.Add(new DxgiAdapterInfo(
                        (int)index,
                        desc.Description,
                        desc.DedicatedVideoMemory,
                        desc.Flags.HasFlag(AdapterFlags.Software),
                        desc.VendorId,
                        desc.DeviceId));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DXGI 显卡枚举失败");
        }

        return adapters;
    }

    /// <summary>
    /// 过滤软件适配器，并按 PCI VendorId+DeviceId 去重（同芯片多块 DXGI 句柄只保留索引最小者）。
    /// </summary>
    public static IReadOnlyList<DxgiAdapterInfo> EnumerateUniqueHardware()
    {
        var seen = new HashSet<(uint VendorId, uint DeviceId)>();
        var unique = new List<DxgiAdapterInfo>();

        foreach (var adapter in Enumerate())
        {
            if (adapter.IsSoftware)
                continue;

            if (!seen.Add((adapter.VendorId, adapter.DeviceId)))
                continue;

            unique.Add(adapter);
        }

        return unique;
    }

    /// <summary>自动模式设备优先级分数（越高越优先）。独显 &gt; 集显，显存大者优先。</summary>
    public static long ScoreForAutoSelect(IReadOnlyList<DxgiAdapterInfo> allAdapters, int deviceId)
    {
        var adapter = allAdapters.FirstOrDefault(a => a.Index == deviceId);
        if (adapter is null)
            return 0;

        long score = (long)Math.Min(adapter.DedicatedVideoMemory, 1L << 40);

        score += adapter.VendorId switch
        {
            VendorNvidia => 1L << 50,
            VendorAmd => 1L << 49,
            VendorIntel => -(1L << 45),
            _ => 0
        };

        return score;
    }

    public static string FormatDisplayName(DxgiAdapterInfo adapter)
    {
        if (adapter.DedicatedVideoMemory >= 256 * 1024 * 1024)
        {
            var gb = adapter.DedicatedVideoMemory / (1024.0 * 1024 * 1024);
            return $"{adapter.Name} · {gb:F1} GB";
        }

        return adapter.Name;
    }
}
