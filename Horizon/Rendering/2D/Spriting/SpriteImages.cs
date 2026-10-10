using Horizon.Logging;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Horizon.Rendering.Spriting;

/// <summary>Some pixels of a sprite, RGBA from the top left with nothing between the rows.</summary>
public readonly record struct SpritePixels(byte[] Data, int Width, int Height);

/// <summary>
/// Where the pixels of sprite images come from, for the atlas and for whoever wants to look at a frame. A PNG (or
/// whatever ImageSharp reads) is read whole and kept for a while, an Aseprite file is asked for its frames, laid
/// side by side as <see cref="SpriteSheetDefinition.FromAseprite"/> says.
/// </summary>
internal static class SpriteImages
{
    // Sheets read lately, by path. Sprite sheets are small and asked for in bursts (every frame of an animation), so a
    // few are kept rather than read off disk once per frame of art
    private const int KEPT = 8;
    private static readonly List<(string Path, SpritePixels Image)> kept = [];

    /// <summary>The whole of an image, null (and logged) if it can't be read. Aseprite files aren't whole images, see <see cref="Read"/>.</summary>
    public static SpritePixels? Load(string path)
    {
        lock (kept)
        {
            for (int i = 0; i < kept.Count; i++)
            {
                if (kept[i].Path != path) continue;

                var hit = kept[i];
                kept.RemoveAt(i);
                kept.Add(hit);
                return hit.Image;
            }
        }

        if (!File.Exists(path))
        {
            Log.Error($"[SpriteImages] There is no image at '{path}'.");
            return null;
        }

        try
        {
            using var image = Image.Load<Rgba32>(path);

            byte[] pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            var read = new SpritePixels(pixels, image.Width, image.Height);

            lock (kept)
            {
                kept.Add((path, read));
                if (kept.Count > KEPT) kept.RemoveAt(0);
            }

            return read;
        }
        catch (Exception e)
        {
            Log.Error($"[SpriteImages] '{path}' couldn't be read: {e.Message}");
            return null;
        }
    }

    /// <summary>Forgets the images kept in memory, for when they've changed on disk.</summary>
    public static void Forget()
    {
        lock (kept) kept.Clear();
    }

    /// <summary>
    /// A part of an image (a frame of an Aseprite file, for a path that is one), copied out. Null if it can't be
    /// read or isn't inside the image, which has been logged.
    /// </summary>
    public static SpritePixels? Read(string path, int x, int y, int width, int height)
    {
        if (width < 1 || height < 1) return null;

        if (AsepriteDocument.IsAseprite(path))
        {
            AsepriteDocument document;
            try
            {
                document = AsepriteDocument.Open(path);
            }
            catch (Exception e)
            {
                Log.Error($"[SpriteImages] '{path}' couldn't be read: {e.Message}");
                return null;
            }

            // Frame i lives at x = i * width on the sheet nobody drew
            int frame = document.Width > 0 ? x / document.Width : 0;
            if (frame < 0 || frame >= document.FrameCount || width > document.Width || height > document.Height)
            {
                Log.Error($"[SpriteImages] '{path}' has no frame {frame} of {width} by {height}.");
                return null;
            }

            byte[] whole = document.ReadFrame(frame);
            if (width == document.Width && height == document.Height && x % document.Width == 0 && y == 0)
                return new SpritePixels(whole, width, height);

            return Crop(new SpritePixels(whole, document.Width, document.Height), x % document.Width, y, width, height, path);
        }

        return Load(path) is { } image ? Crop(image, x, y, width, height, path) : null;
    }

    private static SpritePixels? Crop(in SpritePixels image, int x, int y, int width, int height, string path)
    {
        if (x < 0 || y < 0 || x + width > image.Width || y + height > image.Height)
        {
            Log.Error($"[SpriteImages] {x},{y} {width} by {height} is not inside of '{path}' ({image.Width} by {image.Height}).");
            return null;
        }

        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            image.Data.AsSpan(((y + row) * image.Width + x) * 4, width * 4).CopyTo(pixels.AsSpan(row * width * 4));

        return new SpritePixels(pixels, width, height);
    }
}
