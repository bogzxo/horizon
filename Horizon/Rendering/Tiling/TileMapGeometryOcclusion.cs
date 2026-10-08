using System.Numerics;

using Horizon.Graphics;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// The ambient occlusion a map works out of its own geometry, see <see cref="TileMap.GeometryOcclusion"/>. One
/// picture over the whole map that says how open every spot of it is, made on the CPU whenever what blocks light
/// changed, which for most maps is once. The tiles read it where they
/// end up on screen and write it into the G-buffer with whatever their tile set painted,
/// so it costs a frame one more texture read a tile fragment and no marching at all.
/// </summary>
internal sealed class TileMapGeometryOcclusion : IDisposable
{
    private Texture texture = Texture.Invalid;
    private long builtFor = -1;

    /// <summary>The slot of the picture in the bindless table, once there is one.</summary>
    public uint? Slot => texture.IsValid ? texture.BindlessIndex : null;

    /// <summary>
    /// Makes the picture if what it was made for has changed since. Has to run on the render thread.
    /// </summary>
    /// <param name="signature">Anything that is different whenever the picture would be.</param>
    public void Ensure(TileMap map, float reach, float strength, long signature)
    {
        if (texture.IsValid && builtFor == signature) return;
        builtFor = signature;

        // A cell an art pixel where that fits, the edge of a pot is where the artist put it then
        int cells = map.ShadowCellsPerTile;
        int width = map.Width * cells, height = map.Height * cells;
        if (width <= 0 || height <= 0) return;

        var solid = new bool[width * height];
        // By the shape of every tile, the air in the corners of a pot's tile is air. Row 0 of the picture is the
        // top of the map, the world has its Y going up
        foreach (var (x, y) in map.ShadowCasterTexels(cells))
        {
            if (x >= 0 && y >= 0 && x < width && y < height)
                solid[(height - 1 - y) * width + x] = true;
        }

        var smeared = new float[width * height];
        for (int i = 0; i < solid.Length; i++) smeared[i] = solid[i] ? 1.0f : 0.0f;

        // Twice over with a box half the reach wide is a tent, which falls off the way a shadow in a corner does
        // and is a running sum a row, no matter how far it reaches
        float cellWorld = MathF.Max(map.TileSize.X, map.TileSize.Y) / cells;
        int radius = Math.Max(1, (int)MathF.Round(reach * 0.5f / cellWorld));
        var scratch = new float[Math.Max(width, height)];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < height; y++) Smear(smeared, y * width, 1, width, radius, scratch);
            for (int x = 0; x < width; x++) Smear(smeared, x, width, height, radius, scratch);
        }

        // Up against a flat floor half of what is round a spot is solid, and that is the whole of the strength,
        // a corner has more round it and can't get any darker than that. Inside of something solid there is
        // nothing to say, what is drawn there is a face (a crate) and the marched occlusion looks after those
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            float open = solid[i] ? 1.0f : 1.0f - strength * MathF.Min(1.0f, smeared[i] * 2.0f);
            pixels[i] = (byte)MathF.Round(Math.Clamp(open, 0.0f, 1.0f) * 255.0f);
        }

        if (texture.IsValid) texture.Dispose();
        texture = Texture.FromPixels((uint)width, (uint)height, pixels, new TextureDefinition(PixelFormat.R8, Smooth: true));
        texture.Name = "tile map geometry occlusion";
    }

    /// <summary>Helper method to replace a line of values (a row or a column) by the mean of each one's neighbours, what is past the ends being open.</summary>
    private static void Smear(float[] values, int first, int stride, int count, int radius, float[] scratch)
    {
        float sum = 0.0f;
        for (int i = 0; i < Math.Min(radius, count); i++) sum += values[first + i * stride];

        for (int i = 0; i < count; i++)
        {
            if (i + radius < count) sum += values[first + (i + radius) * stride];
            if (i - radius - 1 >= 0) sum -= values[first + (i - radius - 1) * stride];
            scratch[i] = sum / (2 * radius + 1);
        }

        for (int i = 0; i < count; i++) values[first + i * stride] = scratch[i];
    }

    public void Dispose()
    {
        if (texture.IsValid) texture.Dispose();
        texture = Texture.Invalid;
        builtFor = -1;
    }
}
