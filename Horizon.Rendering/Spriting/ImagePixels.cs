using System.Buffers.Binary;
using System.Numerics;

using Bogz.Logging;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// The pixels of an image as they are in memory, RGBA from the top left with 4 bytes a pixel.
/// </summary>
public readonly record struct ImagePixels(byte[] Data, int Width, int Height)
{
    // The last plain image that was read, for as long as anybody still has it. Whoever reads the frames of a sheet one by
    // one asks for the same file every time
    private static readonly Lock lastLock = new();
    private static (string Path, DateTime Written, WeakReference<byte[]> Data, int Width, int Height)? last;

    /// <summary>
    /// Reads the pixels of an image file. A PNG (or anything else ImageSharp reads) is what it is, an Aseprite file is
    /// one of its frames painted with whichever of its layers are asked for. Anywhere the engine takes the path of an
    /// image it goes through here, so an .ase works wherever a .png does. Thread-safe.
    /// </summary>
    /// <param name="frame">Which frame of an Aseprite file, counted from 0. An image without frames only has the one.</param>
    /// <param name="layers">Which layers of an Aseprite file, the ones it was saved showing if left out.</param>
    /// <returns>Null (after logging why) if the file isn't there or can't be read.</returns>
    public static ImagePixels? Load(string path, int frame = 0, LayerSelection layers = default)
    {
        if (!File.Exists(path))
        {
            Log.Error($"[ImagePixels] Failed to find image '{path}'!");
            return null;
        }

        if (AsepriteDocument.IsAseprite(path))
        {
            return AsepriteDocument.TryLoad(path, out var document)
                ? new ImagePixels(document.Render(frame, layers), document.Width, document.Height)
                : null;
        }

        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            lock (lastLock)
            {
                if (last is { } kept && kept.Path == path && kept.Written == written && kept.Data.TryGetTarget(out byte[]? data))
                    return new ImagePixels(data, kept.Width, kept.Height);
            }

            using var image = Image.Load<Rgba32>(path);

            byte[] pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);

            lock (lastLock) last = (path, written, new WeakReference<byte[]>(pixels), image.Width, image.Height);
            return new ImagePixels(pixels, image.Width, image.Height);
        }
        catch (Exception e)
        {
            Log.Error($"[ImagePixels] Failed to load image '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// How big an image file is in pixels, without reading the pixels of a PNG to find out. Zero if it isn't there or
    /// is neither a PNG nor an Aseprite file.
    /// </summary>
    public static Vector2 SizeOf(string path)
    {
        try
        {
            if (AsepriteDocument.IsAseprite(path))
                return AsepriteDocument.TryLoad(path, out var document) ? new Vector2(document.Width, document.Height) : Vector2.Zero;

            // Width and height are the first thing in a PNG after its signature and the header's length and name
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (file.Read(header) < header.Length || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
                return Vector2.Zero;

            return new Vector2(BinaryPrimitives.ReadInt32BigEndian(header[16..]), BinaryPrimitives.ReadInt32BigEndian(header[20..]));
        }
        catch (Exception)
        {
            return Vector2.Zero;
        }
    }

    /// <summary>
    /// Copies a part of the image out, anything of it that hangs over the edge comes out see-through.
    /// </summary>
    public byte[] Cut(int x, int y, int width, int height)
    {
        byte[] part = new byte[Math.Max(0, width) * Math.Max(0, height) * 4];

        int fromX = Math.Max(0, x), toX = Math.Min(Width, x + width);
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        {
            if (toX <= fromX) break;

            Data.AsSpan((row * Width + fromX) * 4, (toX - fromX) * 4)
                .CopyTo(part.AsSpan(((row - y) * width + fromX - x) * 4));
        }

        return part;
    }
}
