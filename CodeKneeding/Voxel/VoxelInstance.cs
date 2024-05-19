using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal readonly struct VoxelInstance(in uint datapack0)
{
    public readonly uint DataPack0 { get; init; } = datapack0;

    public static VoxelInstance Encode(in Tile tile, in VoxelFace face, in Vector3D<int> localPosition)
    {
        return new VoxelInstance(
            (uint)(
            (((byte)face) & 0b1111) << 0 |   // 1st= 5 bits for X
            (localPosition.X & 0b11111) << 4 |   // 1st= 5 bits for X
            (localPosition.Y & 0b11111) << 9 |   // 2nd= 5 bits for Y
            (localPosition.Z & 0b11111) << 14 |  // 3rd= 5 bits for Z
            (((byte)tile.Type) & 0b1111) << 19   // 4th= 4 bits for ID
            )
            );
    }
}
