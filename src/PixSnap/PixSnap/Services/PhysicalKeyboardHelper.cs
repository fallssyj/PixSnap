using System.Runtime.InteropServices;

namespace PixSnap.Services;

/// <summary>读取物理键盘状态。WPF <see cref="System.Windows.Input.Keyboard"/> 在滚轮等操作后可能不同步。</summary>
internal static class PhysicalKeyboardHelper
{
    private const int VkSpace = 0x20;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsSpacePhysicallyDown =>
        IsKeyPhysicallyDown(VkSpace);

    public static bool IsKeyPhysicallyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
