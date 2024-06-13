using System.Runtime.InteropServices;

namespace CodeKneading.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal struct TileChunkDrawCommand
{
    public uint count;
    public uint instanceCount;
    public uint firstIndex;
    public int firstVertex;
    public uint baseInstance;


    public int chunkCenterX;
    public int chunkCenterY;
    public int chunkCenterZ;
    public int lodLevel;
}
