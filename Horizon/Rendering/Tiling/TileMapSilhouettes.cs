using SixLabors.ImageSharp.PixelFormats;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// Which texels of the images of a map's tile sets have anything in them, kept on the CPU for whoever has to know
/// the shape of a tile and not just the square it sits in, see <see cref="TileMap.ShadowCasterTexels"/>. A pot is
/// not a square, and light that is told it is one goes wrong round every pot. Read off the file once an image,
/// the first time somebody asks.
/// </summary>
internal sealed class TileMapSilhouettes
{
    private sealed record Mask(int Width, int Height, bool[] Filled);

    private readonly Dictionary<string, Mask?> masks = [];

    /// <summary>
    /// Whether a texel of an image has anything in it. An image that can't be read is taken to be full all over,
    /// which is the square the tile was before anybody looked.
    /// </summary>
    public bool Filled(string path, float x, float y)
    {
        if (!masks.TryGetValue(path, out Mask? mask))
            masks[path] = mask = Read(path);

        if (mask is null) return true;

        int column = Math.Clamp((int)MathF.Floor(x), 0, mask.Width - 1);
        int row = Math.Clamp((int)MathF.Floor(y), 0, mask.Height - 1);
        return mask.Filled[column + row * mask.Width];
    }

    private static Mask? Read(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);

            // Half there counts as there, the soft edge of a thing still blocks most of what hits it
            var filled = new bool[image.Width * image.Height];
            for (int i = 0; i < filled.Length; i++) filled[i] = pixels[i * 4 + 3] >= 128;

            return new Mask(image.Width, image.Height, filled);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
