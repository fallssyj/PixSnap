using System.Drawing;

namespace PixSnap.Models;

public sealed class ScreenInfo
{
    public required int Index { get; init; }
    public required string DisplayName { get; init; }
    public required Rectangle Bounds { get; init; }

    public override string ToString() => DisplayName;
}