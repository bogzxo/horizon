using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal readonly struct VoxelVertex(in uint datapack0)
{
    public readonly uint DataPack0 { get; init; } = datapack0;

    public static uint EncodeVertexID(in TileType type) => EncodeVertexID(0, type);
    public static uint EncodeVertexID(in uint baseVertex, in TileType type)
    {
        return baseVertex | (uint)(
            (((byte)type - 2) & 0b1111) << 22  // 4th= 4 bits for ID
        );
    }

    public static uint EncodeLod(in int lod) => EncodeLod(0, lod);
    public static uint EncodeLod(in uint baseVertex, in int lod)
    {
        return baseVertex | (uint)(
            (lod & 0b111) << 28
        );
    }

    public static uint EncodeTexCoords(Vector2D<int> coords) => EncodeTexCoords(0, coords);
    public static uint EncodeTexCoords(in uint baseVertex, Vector2D<int> coords)
    {
        return baseVertex | (uint)(
            ((coords.X & 0b1)  << 26 |  // 5th= 1 bits for texCoords.x
            (coords.Y & 0b1) << 27)    // 6th= 1 bits for texCoords.y
        );
    }

    public static uint EncodeVertexPosition(Vector3D<int> localPosition) => EncodeVertexPosition(0, localPosition);
    public static uint EncodeVertexPosition(in uint baseVertex, Vector3D<int> localPosition)
    {
        return baseVertex | (uint)(
            (localPosition.X & 0b111111) << 4 |     // 1= 6 bits for X
            (localPosition.Y & 0b111111) << 10 |    // 2nd= 6 bits for Y
            (localPosition.Z & 0b111111) << 16      // 3rd= 6 bits for Z
        );
    }

}
