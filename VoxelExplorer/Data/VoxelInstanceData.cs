using System.Runtime.InteropServices;

using Horizon.Core.Data;

using Silk.NET.OpenGL;

namespace VoxelExplorer.Data;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct VoxelInstanceData(in int packedData)
{
    //public readonly Vector3 Position = position;
    //public readonly int Face = face;

    [VertexLayout(0, VertexAttribPointerType.Int)]
    private readonly int PackedData = packedData;

    public static VoxelInstanceData Encode(in int x, in int y, in int z, in int face)
    {
        return new VoxelInstanceData(
            (face & 0b1111)   // 4 bits for face
            | ((x & 0b11111) << 4)     // 5 bits for x
            | ((y & 0b11111) << 9)     // 5 bits for y
            | ((z & 0b11111) << 14));  // 5 bits for z
    }
}