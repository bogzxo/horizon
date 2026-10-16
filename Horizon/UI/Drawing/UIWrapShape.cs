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
    /// The shape of what is drawn in a picture, RGBA from its top left. Grown by so many pixels every way, so the text
    /// keeps off it a little and a stray pixel of hair doesn't cut a line short on its own row only.
    /// </summary>
    public static UIWrapShape FromPixels(ReadOnlySpan<byte> rgba, int width, int height, int grow = 2, byte solid = SOLID)
    {
        if (width < 1 || height < 1 || rgba.Length < width * height * 4)
            return new UIWrapShape([], []);

        // Row by row, the first and the last pixel that is anything
        var first = new int[height];
        var last = new int[height];
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

            first[y] = from;
            last[y] = to;
        }

        // Grown up and down by taking the widest of the rows round each, and along by the same
        grow = Math.Max(grow, 0);
        var left = new float[height];
        var right = new float[height];
        for (int y = 0; y < height; y++)
        {
            int from = int.MaxValue, to = -1;
            for (int near = Math.Max(0, y - grow); near <= Math.Min(height - 1, y + grow); near++)
            {
                if (first[near] < 0) continue;
                from = Math.Min(from, first[near]);
                to = Math.Max(to, last[near]);
            }

            if (to < 0)
            {
                (left[y], right[y]) = (1.0f, 0.0f);
                continue;
            }

            left[y] = Math.Max(0, from - grow) / (float)width;
            right[y] = Math.Min(width, to + 1 + grow) / (float)width;
        }

        return new UIWrapShape(left, right);
    }

    /// <summary>
    /// The shape of what is drawn in a picture file (anything ImageSharp reads, a .png with its see-through). Null if it
    /// can't be read, which is logged. Off the render thread is fine, nothing of it goes near the GPU.
    /// </summary>
    public static UIWrapShape? FromFile(string path, int grow = 2, byte solid = SOLID)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            return FromPixels(pixels, image.Width, image.Height, grow, solid);
        }
        catch (Exception exception)
        {
            Log.Warning($"[UIWrapShape] Couldn't read the shape of '{path}': {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// How far across the shape goes between two heights, put in a box (the UI's y going up), the leftmost and the
    /// rightmost of every row of it that is between them. False when none of it is.
    /// </summary>
    public bool Across(UIRect placed, float bottom, float top, out float from, out float to)
    {
        from = float.MaxValue;
        to = float.MinValue;
        if (Rows == 0 || placed.Width <= 0.0f || placed.Height <= 0.0f || top <= placed.Min.Y || bottom >= placed.Max.Y)
            return false;

        // The rows the heights fall on, row 0 being the top of the box
        float rowHeight = placed.Height / Rows;
        int firstRow = Math.Clamp((int)((placed.Max.Y - top) / rowHeight), 0, Rows - 1);
        int lastRow = Math.Clamp((int)MathF.Ceiling((placed.Max.Y - bottom) / rowHeight) - 1, 0, Rows - 1);

        for (int row = firstRow; row <= lastRow; row++)
        {
            if (left[row] > right[row]) continue;
            from = MathF.Min(from, placed.Min.X + left[row] * placed.Width);
            to = MathF.Max(to, placed.Min.X + right[row] * placed.Width);
        }

        return to >= from;
    }
}

/// <summary>
/// A shape put somewhere, for the text of something to wrap around. Where is up to whoever takes it, the code editor
/// goes by its text area, from its bottom left corner (the UI's y going up).
/// </summary>
public readonly record struct UIWrapArea(UIWrapShape Shape, UIRect Bounds);
