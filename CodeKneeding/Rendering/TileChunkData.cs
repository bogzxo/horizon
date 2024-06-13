using System.Runtime.InteropServices;

namespace CodeKneading.Rendering;

[StructLayout(LayoutKind.Sequential)]
public readonly struct TileChunkData
{
    public int xPos { get; init; }
    public int yPos { get; init; }
    public int zPos { get; init; }
    public int face { get; init; }
}
