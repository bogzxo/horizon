using System.Numerics;

namespace Horizon.Rendering.Voxels;

/// <summary>What a voxel is made of, as far as light and bumping into it go.</summary>
public enum VoxelKind : byte
{
    /// <summary>Nothing. Material 0 is always this.</summary>
    Air,

    /// <summary>A cube, solid, opaque, the bulk of everything.</summary>
    Solid,

    /// <summary>Water. See through, reflects the sky, nobody stands on it, they swim.</summary>
    Liquid,

    /// <summary>
    /// A little model inside the block (grass, a flower, a mushroom), <see cref="VoxelMaterial.Model"/> says which.
    /// Light and rays go through the gaps, feet go through all of it.
    /// </summary>
    Foliage,

    /// <summary>Leaves. Solid to walk on, but full of holes a quarter of a block across that the sun comes through in spots.</summary>
    Leaves,

    /// <summary>A cube the light goes through, tinted by its colour.</summary>
    Glass
}

/// <summary>
/// One material of a <see cref="VoxelPalette"/>, which is what a voxel is (a voxel is one byte, the index of its
/// material). The look of it for the path tracer, the kind of thing it is for light and for feet.
/// </summary>
public struct VoxelMaterial
{
    /// <summary>What it's called, for the debug text and the save file.</summary>
    public string Name;

    public VoxelKind Kind;

    /// <summary>
    /// Its colour in linear light, used on its own when the voxel has no tile, far away where a tile is smaller than a
    /// pixel (it should be the average of the tile, see <see cref="VoxelPalette.AverageTiles"/>), and for the far
    /// away version of whatever it is part of.
    /// </summary>
    public Vector3 Color;

    /// <summary>The tile of the atlas on top, on the sides and underneath, <see cref="VoxelPalette.NO_TILE"/> for the plain colour.</summary>
    public byte TopTile, SideTile, BottomTile;

    /// <summary>The light it gives off, linear, can be well over 1. A lamp.</summary>
    public Vector3 Emission;

    /// <summary>How rough it is, 1 for chalk and 0 for a mirror. Only the water and anything under about 0.3 show reflections.</summary>
    public float Roughness;

    /// <summary>For <see cref="VoxelKind.Foliage"/>, which of the palette's <see cref="VoxelPalette.Models"/> it is.</summary>
    public byte Model;

    /// <summary>Whether it moves in the wind, which is foliage and leaves mostly.</summary>
    public bool Sways;

    public readonly bool IsSolid => Kind is VoxelKind.Solid or VoxelKind.Leaves or VoxelKind.Glass;

    public static VoxelMaterial Cube(string name, Vector3 color, byte tile = VoxelPalette.NO_TILE) => new()
    {
        Name = name,
        Kind = VoxelKind.Solid,
        Color = color,
        TopTile = tile,
        SideTile = tile,
        BottomTile = tile,
        Roughness = 1.0f
    };
}

/// <summary>
/// The 256 materials a <see cref="VoxelTree"/> can be made of, the atlas their tiles are in, and the little models
/// foliage is made out of. Material 0 is air and stays air. What the path tracer reads it as is four numbers a
/// material (<see cref="Pack"/>) and the models as a few dozen each (<see cref="VoxelModel.Pack"/>).
/// </summary>
public sealed class VoxelPalette
{
    /// <summary>A tile number that means no tile, the plain colour.</summary>
    public const byte NO_TILE = 255;

    public const int CAPACITY = 256;

    private readonly VoxelMaterial[] materials = new VoxelMaterial[CAPACITY];
    private int count = 1;

    /// <summary>The little models of the foliage, by the index a material names.</summary>
    public List<VoxelModel> Models { get; } = [];

    /// <summary>How many tiles the atlas has a row, it is square and so are they.</summary>
    public int AtlasTilesPerRow { get; set; } = 16;

    public VoxelPalette()
    {
        materials[0] = new VoxelMaterial { Name = "air", Kind = VoxelKind.Air };
    }

    public int Count => count;

    public ref VoxelMaterial this[int index] => ref materials[index];

    /// <summary>Puts a material in the next free place and says which it got.</summary>
    public byte Add(in VoxelMaterial material)
    {
        if (count >= CAPACITY) throw new InvalidOperationException("A palette holds 256 materials and that's that, a voxel is one byte.");

        materials[count] = material;
        return (byte)count++;
    }

    /// <summary>The material by its name, 0 (air) if there is none of that name.</summary>
    public byte Find(string name)
    {
        for (int i = 1; i < count; i++)
        {
            if (string.Equals(materials[i].Name, name, StringComparison.OrdinalIgnoreCase)) return (byte)i;
        }

        return 0;
    }

    /// <summary>
    /// Sets every tiled material's colour to the average of its tiles, so it looks the same far away as close up.
    /// <paramref name="rgba"/> is the atlas, four bytes a pixel, sRGB.
    /// </summary>
    public void AverageTiles(ReadOnlySpan<byte> rgba, int atlasSize)
    {
        int tile = atlasSize / AtlasTilesPerRow;
        for (int i = 1; i < count; i++)
        {
            ref VoxelMaterial m = ref materials[i];
            if (m.TopTile == NO_TILE && m.SideTile == NO_TILE) continue;

            // The top counts for more, it's what is seen of most things from far away
            Vector3 sum = Vector3.Zero;
            float weight = 0.0f;
            if (TileAverage(rgba, atlasSize, tile, m.TopTile) is { } top)
            {
                sum += top * 3.0f;
                weight += 3.0f;
            }

            if (TileAverage(rgba, atlasSize, tile, m.SideTile) is { } side)
            {
                sum += side;
                weight += 1.0f;
            }

            if (weight > 0.0f) m.Color = sum / weight;
        }
    }

    /// <summary>The same, the atlas read from a file (a PNG, or anything else ImageSharp or the Aseprite reader takes). Nothing happens if it can't be read.</summary>
    public void AverageTiles(string atlasPath)
    {
        if (!File.Exists(atlasPath)) return;

        using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(atlasPath);
        if (image.Width != image.Height) return;

        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        AverageTiles(rgba, image.Width);
    }

    /// <summary>Helper method for the average colour of a tile of the atlas, linear, leaving out the see through texels. Null for no tile.</summary>
    private Vector3? TileAverage(ReadOnlySpan<byte> rgba, int atlasSize, int tile, byte index)
    {
        if (index == NO_TILE) return null;

        int ox = index % AtlasTilesPerRow * tile, oy = index / AtlasTilesPerRow * tile;
        Vector3 sum = Vector3.Zero;
        int seen = 0;
        for (int y = 0; y < tile; y++)
        {
            for (int x = 0; x < tile; x++)
            {
                int at = ((oy + y) * atlasSize + ox + x) * 4;
                if (rgba[at + 3] < 128) continue;
                sum += new Vector3(ToLinear(rgba[at]), ToLinear(rgba[at + 1]), ToLinear(rgba[at + 2]));
                seen++;
            }
        }

        return seen == 0 ? null : sum / seen;
    }

    /// <summary>
    /// The materials as the path tracer reads them, four numbers each. The colour as sRGB bytes with the kind in the
    /// top byte, the three tiles and the model, the light it gives off as three halves, and the roughness and the sway.
    /// </summary>
    public uint[] Pack()
    {
        var packed = new uint[CAPACITY * 4];
        for (int i = 0; i < CAPACITY; i++)
        {
            VoxelMaterial m = materials[i];
            packed[i * 4 + 0] = PackColor(m.Color) | (uint)m.Kind << 24;
            packed[i * 4 + 1] = m.TopTile | (uint)m.SideTile << 8 | (uint)m.BottomTile << 16 | (uint)m.Model << 24;
            packed[i * 4 + 2] = Half(m.Emission.X) | Half(m.Emission.Y) << 16;
            packed[i * 4 + 3] = Half(m.Emission.Z) | (uint)(Math.Clamp(m.Roughness, 0.0f, 1.0f) * 255.0f) << 16 | (m.Sways ? 1u << 24 : 0u);
        }

        return packed;
    }

    /// <summary>A linear colour as sRGB bytes, red in the bottom.</summary>
    public static uint PackColor(Vector3 linear) =>
        ToSrgbByte(linear.X) | ToSrgbByte(linear.Y) << 8 | ToSrgbByte(linear.Z) << 16;

    public static float ToLinear(byte srgb)
    {
        float c = srgb / 255.0f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>An sRGB colour (bytes, the way paint programs give them) in linear light.</summary>
    public static Vector3 Srgb(byte r, byte g, byte b) => new(ToLinear(r), ToLinear(g), ToLinear(b));

    private static uint ToSrgbByte(float linear)
    {
        linear = Math.Clamp(linear, 0.0f, 1.0f);
        float c = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1.0f / 2.4f) - 0.055f;
        return (uint)MathF.Round(c * 255.0f);
    }

    private static uint Half(float value) => BitConverter.HalfToUInt16Bits((Half)value);
}
