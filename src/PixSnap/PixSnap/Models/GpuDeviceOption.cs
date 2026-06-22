namespace PixSnap.Models;

/// <summary>设置页 AI GPU 下拉项。DeviceId：-2 自动，-1 仅 CPU，&gt;=0 为 DirectML 设备索引。</summary>
public sealed class GpuDeviceOption(int deviceId, string displayName)
{
    public int DeviceId { get; } = deviceId;

    public string DisplayName { get; } = displayName;

    public override string ToString() => DisplayName;
}
