using System.Numerics;

namespace Horizon.Rendering.Voxels;

/// <summary>
/// A little model that lives inside one block, eight voxels a side, for the things too small to be blocks. A tuft of
/// grass, a flower, a mushroom, a pebble. Every voxel of it is one of three colours or nothing, the path tracer goes
/// through it like it goes through the world, so it casts a shadow of its own and the light comes through the gaps.
/// Every block of foliage in the world turns its model round by where it is, so a meadow isn't the same tuft a
/// thousand times over in a grid.
/// <code>
/// var tuft = new VoxelModel(VoxelPalette.Srgb(70, 140, 40), VoxelPalette.Srgb(95, 170, 55));
/// tuft.Set(3, 0, 3, 1); tuft.Set(3, 1, 3, 2);
/// palette.Models.Add(tuft);
/// </code>
/// </summary>
public sealed class VoxelModel
{
    public const int SIZE = 8;

    /// <summary>How many numbers a model is to the path tracer, two bits a voxel and the three colours.</summary>
    public const int PACKED_UINTS = SIZE * SIZE * SIZE * 2 / 32 + 4;

    private readonly byte[] cells = new byte[SIZE * SIZE * SIZE];

    /// <summary>The three colours, linear. A voxel of colour 1 is the first, 0 is nothing.</summary>
    public Vector3[] Colors { get; } = new Vector3[3];

    public VoxelModel(Vector3 first, Vector3? second = null, Vector3? third = null)
    {
        Colors[0] = first;
        Colors[1] = second ?? first;
        Colors[2] = third ?? second ?? first;
    }

    /// <summary>Which colour a voxel is, 0 for nothing. X across, Y up, Z deep.</summary>
    public int Get(int x, int y, int z) => In(x, y, z) ? cells[Index(x, y, z)] : 0;

    public void Set(int x, int y, int z, int color)
    {
        if (In(x, y, z)) cells[Index(x, y, z)] = (byte)Math.Clamp(color, 0, 3);
    }

    /// <summary>Whether the model has anything in it at all.</summary>
    public bool IsEmpty => Array.TrueForAll(cells, c => c == 0);

    /// <summary>
    /// The model as the path tracer reads it. Two bits a voxel in the order X, then Z, then Y (sixteen to a number),
    /// then the three colours as sRGB bytes and a spare.
    /// </summary>
    public void Pack(Span<uint> into)
    {
        into[..PACKED_UINTS].Clear();
        for (int i = 0; i < cells.Length; i++)
            into[i >> 4] |= (uint)cells[i] << ((i & 15) * 2);

        int colors = SIZE * SIZE * SIZE * 2 / 32;
        for (int c = 0; c < 3; c++)
            into[colors + c] = VoxelPalette.PackColor(Colors[c]);
    }

    private static bool In(int x, int y, int z) => (uint)x < SIZE && (uint)y < SIZE && (uint)z < SIZE;

    private static int Index(int x, int y, int z) => x + z * SIZE + y * SIZE * SIZE;
}
