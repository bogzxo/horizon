namespace Horizon.Graphics;

/// <summary>A picture as its pixels, four bytes a pixel (red, green, blue, alpha), the top row first.</summary>
public readonly record struct ImagePixels(int Width, int Height, byte[] Pixels);

/// <summary>
/// Pictures read off the disk and written back, PNG, JPEG, BMP, TGA, GIF (its first frame), PSD and HDR in, PNG out.
/// It's stb_image and stb_image_write in C# (StbImageSharp and StbImageWriteSharp, public domain, no unmanaged memory
/// pooled behind our back and nothing a native exe can't have). It was ImageSharp, whose fourth version wants a licence
/// key from Six Labors to build at all, and the third has holes in it nobody's going to patch.
/// </summary>
public static class ImageFile
{
    /// <summary>A picture's pixels, whatever it was saved as. Throws when the file isn't a picture it can read.</summary>
    public static ImagePixels Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>A picture's pixels out of a stream.</summary>
    public static ImagePixels Load(Stream stream)
    {
        var read = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return new ImagePixels(read.Width, read.Height, read.Data);
    }

    /// <summary>
    /// Writes pixels as a PNG.
    /// </summary>
    /// <param name="bottomUp">Whether the pixels are the bottom row first (what comes back off the GPU), turned the right way up on the way.</param>
    public static void SavePng(string path, byte[] pixels, int width, int height, bool bottomUp = false)
    {
        byte[] written = pixels;
        if (bottomUp)
        {
            int row = width * 4;
            written = new byte[pixels.Length];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * row, written, (height - 1 - y) * row, row);
        }

        using FileStream stream = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(written, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }
}
