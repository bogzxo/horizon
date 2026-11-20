using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using Horizon.Content;
using Horizon.Logging;

using StbTrueTypeSharp;

namespace Horizon.Rendering.Text;

/// <summary>
/// Where a glyph's picture went and how it sits on the pen, in whole pixels of the screen. <see cref="Left"/> is from the
/// pen's pixel to the picture's left column, <see cref="Top"/> from the baseline's row down to its top row (negative,
/// the picture is above the line). An empty glyph, a space, is all naughts.
/// </summary>
public readonly record struct GlyphBox(int Left, int Top, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// A TrueType font's outlines, for drawing its letters as pictures of the exact size they are on the screen rather than
/// out of a distance field made at one size for all of them. A distance field drawn small is soft and heavy: its edge is
/// put back together from a picture with three times the texels of the letter, and every stem lands where it falls
/// between the screen's pixels. Here a letter is made at the size it's drawn, its x height on a whole row of pixels
/// (the light hinting FreeType does, which is what makes small letters crisp), at a quarter of a pixel across, bold by
/// smearing the ink across as far as the bold letter is wider and italic by leaning the outline before it's filled.
/// <para>One for every font file, shared by everybody drawing it, see <see cref="For"/>. Any thread.</para>
/// </summary>
public sealed unsafe class GlyphOutlines
{
    // The font file, out of the collector's way for good: a font is read for as long as anything draws with it
    private readonly byte* data;
    private readonly StbTrueType.stbtt_fontinfo info = new();

    // Font units to pixels for a font a pixel tall, the same measure DistanceFieldFont's metrics are made in
    private readonly float unitsPerHeight;

    // How tall an x is, in font units, 0 for a font without one (an icon font)
    private readonly float xHeight;

    // Character to the font's glyph, looked up once. Rasterize is only ever called under the glyph atlas's lock, so a
    // plain dictionary, one lock guarding it rather than two schemes
    private readonly Dictionary<char, int> indices = [];

    /// <summary>A number of its own, for keys.</summary>
    public int Id { get; }

    private static int made;
    // Lazy so two threads asking for the same font at once read it once, a factory GetOrAdd ran twice left a copy behind
    private static readonly ConcurrentDictionary<string, Lazy<GlyphOutlines?>> known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far an italic letter leans, across for every unit up. The same as the field's, see UIFont.ITALIC_SLANT.</summary>
    public const float ITALIC_SLANT = 0.2f;

    private GlyphOutlines(byte[] bytes)
    {
        // Kept for good once it reads, a font is drawn with for as long as Hex runs. Given back if it doesn't
        data = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        if (StbTrueType.stbtt_InitFont(info, data, StbTrueType.stbtt_GetFontOffsetForIndex(data, 0)) == 0)
        {
            NativeMemory.Free(data);
            throw new InvalidDataException("not a TrueType font stb_truetype can read");
        }

        unitsPerHeight = StbTrueType.stbtt_ScaleForPixelHeight(info, 1.0f);
        int x0, y0, x1, y1;
        int x = StbTrueType.stbtt_FindGlyphIndex(info, 'x');
        if (x != 0 && StbTrueType.stbtt_GetGlyphBox(info, x, &x0, &y0, &x1, &y1) != 0)
            xHeight = y1;
        Id = Interlocked.Increment(ref made);
    }

    /// <summary>The outlines of a TrueType font file (<c>.ttf</c>, <c>.otf</c>), null for anything else or a file that won't read.</summary>
    public static GlyphOutlines? For(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".ttf" or ".otf"))
            return null;

        // A file that isn't there yet is looked for again next time rather than remembered as missing
        if (!ContentFiles.Exists(path))
            return null;

        return known.GetOrAdd(ContentFiles.CacheKey(path), _ => new Lazy<GlyphOutlines?>(() =>
        {
            try
            {
                return new GlyphOutlines(ContentFiles.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                Log.Error($"[GlyphOutlines] '{path}' would not load, {e.Message}");
                return null;
            }
        })).Value;
    }

    // How much taller or shorter the letters are made so the top of an x lands on a whole row of pixels. Rounded a little
    // up rather than to the nearest, a taller x height reads better small, and never by more than an eighth
    private float Hinted(float scale)
    {
        float tall = xHeight * scale;
        if (tall < 3.0f)
            return 1.0f;
        return Math.Clamp(MathF.Round(tall + 0.15f) / tall, 0.88f, 1.12f);
    }

    /// <summary>
    /// Fills a letter in, as many pixels of coverage as <paramref name="box"/> says, a row after another. Not safe on two
    /// threads at once, the glyph atlas calls it under its lock. The pixels go
    /// into <paramref name="into"/>, which is made bigger when it has to be.
    /// </summary>
    /// <param name="pixelHeight">How tall the font is drawn, ascent to descent, in pixels of the screen.</param>
    /// <param name="shift">How far into its first pixel the pen is, 0 to 1.</param>
    /// <param name="bold">How many pixels further across the ink is smeared, 0 for none.</param>
    /// <param name="slant">How far the letter leans across for every unit up, 0 for upright.</param>
    /// <returns>Whether there is anything to draw.</returns>
    public bool Rasterize(char character, float pixelHeight, float shift, float bold, float slant, ref byte[] into, out GlyphBox box)
    {
        box = default;
        if (!indices.TryGetValue(character, out int glyph))
            indices[character] = glyph = StbTrueType.stbtt_FindGlyphIndex(info, character);
        if (glyph == 0 && character != '?')
            return false;

        StbTrueType.stbtt_vertex* vertices;
        int count = StbTrueType.stbtt_GetGlyphShape(info, glyph, &vertices);
        if (count <= 0 || vertices == null)
            return false;

        try
        {
            float scale = unitsPerHeight * pixelHeight;
            float scaleY = scale * Hinted(scale);

            // Leant over about the baseline, the outline's own numbers moved across, so it's filled in leaning
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                ref StbTrueType.stbtt_vertex v = ref vertices[i];
                if (slant != 0.0f)
                {
                    v.x = (short)Math.Clamp(MathF.Round(v.x + slant * v.y), short.MinValue, short.MaxValue);
                    v.cx = (short)Math.Clamp(MathF.Round(v.cx + slant * v.cy), short.MinValue, short.MaxValue);
                    v.cx1 = (short)Math.Clamp(MathF.Round(v.cx1 + slant * v.cy1), short.MinValue, short.MaxValue);
                }

                Grow(v.x, v.y);
                if (v.type is 3 or 4)
                    Grow(v.cx, v.cy);
                if (v.type == 4)
                    Grow(v.cx1, v.cy1);
            }

            void Grow(float x, float y)
            {
                minX = MathF.Min(minX, x);
                maxX = MathF.Max(maxX, x);
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
            }

            if (minX > maxX || minY > maxY)
                return false;

            int left = (int)MathF.Floor(minX * scale + shift);
            int right = (int)MathF.Ceiling(maxX * scale + shift + bold);
            int top = (int)MathF.Floor(-maxY * scaleY);
            int bottom = (int)MathF.Ceiling(-minY * scaleY);
            int width = right - left, height = bottom - top;
            if (width <= 0 || height <= 0)
                return false;

            int size = width * height;
            if (into.Length < size)
                into = new byte[Math.Max(size, into.Length * 2)];
            Array.Clear(into, 0, size);

            fixed (byte* pixels = into)
            {
                var bitmap = new StbTrueType.stbtt__bitmap { w = width, h = height, stride = width, pixels = pixels };
                StbTrueType.stbtt_Rasterize(&bitmap, 0.35f, vertices, count, scale, scaleY, shift, 0.0f, left, top, 1, null, false);
            }

            if (bold > 0.0f)
                Embolden(into.AsSpan(0, size), width, height, bold);

            box = new GlyphBox(left, top, width, height);
            return true;
        }
        finally
        {
            StbTrueType.stbtt_FreeShape(info, vertices);
        }
    }

    // Every row's ink smeared across to the right by so many pixels and a share of one, the brightest of what's behind
    // it, which is how FreeType makes a letter bold that has no bold of its own. Right to left so it reads what it hasn't
    // changed yet
    private static void Embolden(Span<byte> pixels, int width, int height, float bold)
    {
        int whole = (int)bold;
        float part = bold - whole;
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = pixels.Slice(y * width, width);
            for (int x = width - 1; x >= 0; x--)
            {
                int ink = row[x];
                for (int k = 1; k <= whole && x - k >= 0; k++)
                    ink = Math.Max(ink, row[x - k]);
                if (part > 0.0f && x - whole - 1 >= 0)
                    ink = Math.Max(ink, (int)(row[x - whole - 1] * part + 0.5f));
                row[x] = (byte)ink;
            }
        }
    }
}
