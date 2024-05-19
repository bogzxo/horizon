using System.Numerics;
using System.Runtime.InteropServices;

using AutoVoxel.World;

using Horizon.Core.Data;

using Silk.NET.OpenGL;

namespace AutoVoxel.Data;

public enum UVCoordinate
{
    TopLeft = 0,
    BottomLeft = 1,
    TopRight = 2,
    BottomRight = 3
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct ChunkVertex(in int packedData)
{
    [VertexLayout(0, VertexAttribPointerType.Int)]
    private readonly int PackedData = packedData;

    public static ChunkVertex Encode(in int x, in int y, in int z, in CubeFace face, in TileID tileId)
    {
        return new ChunkVertex(
            ((int)face & 0b1111)   // 4 bits for face
            | ((x & 0b11111) << 4)     // 5 bits for x
            | ((y & 0b11111) << 9)     // 5 bits for y
            | ((z & 0b11111) << 14)  // 5 bits for z
            | (((int)(tileId - 2) & 0b11111) << 19));  // 5 bits for id
    }
}