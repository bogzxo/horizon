using System.Numerics;
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
        return new VoxelInstanceData(((31 & x) << 0 | (31 & y) << 5 | (31 & z) << 10 | (6 & face) << 15));
    }
}
