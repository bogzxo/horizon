using System.Numerics;

using Horizon.Logging;

using SixLabors.ImageSharp.PixelFormats;

namespace Horizon.UI;

/// <summary>
/// A shape text wraps around, the way a magazine runs its columns round a picture. It is how wide something is on every
/// row of it, from the leftmost of it to the rightmost, as shares of its width, so it fits whatever box it is put in
/// (<see cref="UIWrapArea"/>). Made out of the pixels of a picture (<see cref="FromPixels"/>, <see cref="FromFile"/>), where
/// every pixel that is more than a little opaque is in it and the see-through around a character isn't, the way
/// Fighter2D's box tracer reads a frame, or a plain box (<see cref="Box"/>). A row with a hole in the middle still
/// blocks all the way across, text doesn't go in between somebody's arm and their side.
/// <para>
/// The outline is the picture's to the pixel. How far the text keeps off it is a margin asked for with the shape
/// (<see cref="Across"/>), in the UI's own units and round, a line passing over the top of her head kept off it as far
/// as one going past her side, whatever size she is drawn at.
/// </para>
/// <para>
/// The code editor wraps its rows round the shapes it is given when it wraps at all (<c>CodeEditor.WrapAreas</c>,
/// <c>CodeEditor.BackdropShape</c>). Made once, read from any thread, never changed.
/// </para>
/// </summary>
public sealed class UIWrapShape
{
    /// <summary>How much alpha (out of 255) a pixel needs to be part of the shape, the box tracer's number.</summary>
    public const byte SOLID = 32;

    // The leftmost and the rightmost of it on every row, top row first, as shares of the width, an empty row left > right
    private readonly float[] left, right;

    /// <summary>How many rows it has, top to bottom, which is how many pixels tall the picture was.</summary>
    public int Rows => left.Length;

    private UIWrapShape(float[] left, float[] right)
    {
        this.left = left;
        this.right = right;
    }

    /// <summary>The whole of whatever box it is put in.</summary>
    public static UIWrapShape Box() => new([0.0f], [1.0f]);

    /// <summary>
    /// The shape of what is drawn in a picture, RGBA from its top left, every row from the first pixel that is anything
    /// to the last. The edge is where the artist put it, the text keeps off it by the margin it is asked with.
    /// </summary>
    public static UIWrapShape FromPixels(ReadOnlySpan<byte> rgba, int width, int height, byte solid = SOLID)
    {
        if (width < 1 || height < 1 || rgba.Length < width * height * 4)
            return new UIWrapShape([], []);

        var left = new float[height];
        var right = new float[height];
        for (int y = 0; y < height; y++)
        {
            int from = -1, to = -1;
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                if (rgba[row + x * 4 + 3] < solid) continue;
                if (from < 0) from = x;
                to = x;
            }

            (left[y], right[y]) = from < 0 ? (1.0f, 0.0f) : (from / (float)width, (to + 1) / (float)width);
        }

        return new UIWrapShape(left, right);
    }

    /// <summary>
    /// The shape of what is drawn in a picture file (anything ImageSharp reads, a .png with its see-through). Null if it
    /// can't be read, which is logged. Off the render thread is fine, nothing of it goes near the GPU.
    /// </summary>
    public static UIWrapShape? FromFile(string path, byte solid = SOLID)
    {
        try
        {
            // The last picture read is kept, it's read again as often as anybody asks for its shape (a setting changed)
            string full = System.IO.Path.GetFullPath(path);
            long written = System.IO.File.GetLastWriteTimeUtc(full).Ticks;
            lock (lastSync)
            {
                if (last is not { } known || known.Path != full || known.Written != written)
                {
                    using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(full);
                    var read = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(read);
                    last = known = (full, written, read, image.Width, image.Height);
                }

                return FromPixels(known.Pixels, known.Width, known.Height, solid);
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"[UIWrapShape] Couldn't read the shape of '{path}': {exception.Message}");
            return null;
        }
    }

    // The picture FromFile read last, by where it is and when it was written
    private static readonly object lastSync = new();
    private static (string Path, long Written, byte[] Pixels, int Width, int Height)? last;

    /// <summary>
    /// How far across the shape goes between two heights, put in a box (the UI's y going up), kept off by a margin. Every
    /// row of it the margin reaches is counted, as far out as the margin reaches at that height, which is all of it
    /// level with the row and nothing a whole margin above or below, a circle round every edge pixel. False when
    /// none of it is in reach.
    /// </summary>
    /// <param name="margin">How far the text keeps off the shape, in the same units as the box, 0 for right up to it.</param>
    public bool Across(UIRect placed, float bottom, float top, out float from, out float to, float margin = 0.0f)
    {
        from = float.MaxValue;
        to = float.MinValue;
        margin = MathF.Max(0.0f, margin);
        if (Rows == 0 || placed.Width <= 0.0f || placed.Height <= 0.0f || top + margin <= placed.Min.Y || bottom - margin >= placed.Max.Y)
            return false;

        // The rows the heights and the margin above and below them fall on, row 0 being the top of the box
        float rowHeight = placed.Height / Rows;
        int firstRow = Math.Clamp((int)((placed.Max.Y - top - margin) / rowHeight), 0, Rows - 1);
        int lastRow = Math.Clamp((int)MathF.Ceiling((placed.Max.Y - bottom + margin) / rowHeight) - 1, 0, Rows - 1);

        for (int row = firstRow; row <= lastRow; row++)
        {
            if (left[row] > right[row]) continue;

            // How far the row is above or below the heights asked about, nothing for one between them
            float rowTop = placed.Max.Y - row * rowHeight, rowBottom = rowTop - rowHeight;
            float apart = rowBottom >= top ? rowBottom - top : bottom >= rowTop ? bottom - rowTop : 0.0f;
            if (apart > margin) continue;

            float reach = margin > 0.0f ? MathF.Sqrt(margin * margin - apart * apart) : 0.0f;
            from = MathF.Min(from, placed.Min.X + left[row] * placed.Width - reach);
            to = MathF.Max(to, placed.Min.X + right[row] * placed.Width + reach);
        }

        return to >= from;
    }
}

/// <summary>
/// A shape put somewhere, for the text of something to wrap around, and how far the text keeps off it. Where is up to
/// whoever takes it, the code editor goes by its text area, from its bottom left corner (the UI's y going up).
/// </summary>
public readonly record struct UIWrapArea(UIWrapShape Shape, UIRect Bounds, float Margin = 0.0f);
