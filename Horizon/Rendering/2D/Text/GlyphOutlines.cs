using System.Buffers.Binary;
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
    private readonly int dataLength;
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

    // Shared native buffer for a font file, kept for the process lifetime so multiple faces or callers don't duplicate
    // multi-megabyte .ttf/.ttc files in memory
    private readonly record struct NativeFontBuffer(nint Pointer, int Length);
    private static readonly ConcurrentDictionary<string, Lazy<NativeFontBuffer>> sharedBuffers = new(StringComparer.OrdinalIgnoreCase);

    internal byte* NativeData => data;
    internal int NativeDataLength => dataLength;

    private static int made;
    // Lazy so two threads asking for the same font at once read it once, a factory GetOrAdd ran twice left a copy behind
    private static readonly ConcurrentDictionary<string, Lazy<GlyphOutlines?>> known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far an italic letter leans, across for every unit up. The same as the field's, see UIFont.ITALIC_SLANT.</summary>
    public const float ITALIC_SLANT = 0.2f;

    private const uint TTC_TAG = 0x74746366u; // 'ttcf'

    private GlyphOutlines(byte* nativeData, int length, bool ownsMemory, int faceIndex = 0)
    {
        if (!TryGetFontOffset(new ReadOnlySpan<byte>(nativeData, length), faceIndex, out int fontOffset))
        {
            if (ownsMemory)
                NativeMemory.Free(nativeData);
            throw new InvalidDataException("not a TrueType font or collection stb_truetype can read");
        }

        // Kept for good once it reads, a font is drawn with for as long as Hex runs. Given back if it doesn't
        data = nativeData;
        dataLength = length;
        if (StbTrueType.stbtt_InitFont(info, data, fontOffset) == 0)
        {
            if (ownsMemory)
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

    private static GlyphOutlines CreateFromManagedBytes(byte[] bytes, int faceIndex)
    {
        if (!TryGetFontOffset(bytes, faceIndex, out _))
            throw new InvalidDataException("not a TrueType font or collection stb_truetype can read");

        byte* native = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(native, bytes.Length));
        return new GlyphOutlines(native, bytes.Length, ownsMemory: true, faceIndex);
    }

    /// <summary>Creates outlines directly from raw TrueType/OpenType or TrueType Collection (<c>.ttc</c>) bytes.</summary>
    internal static GlyphOutlines FromBytes(byte[] bytes, int faceIndex = 0) => CreateFromManagedBytes(bytes, faceIndex);

    // Helper method to read a font file straight into native memory so large .ttf/.ttc files skip a managed LOH copy
    private static NativeFontBuffer LoadNativeBuffer(string path)
    {
        using Stream stream = ContentFiles.OpenRead(path);
        if (stream.CanSeek && stream.Length - stream.Position is long length && length > 0 && length <= Array.MaxLength)
        {
            int size = (int)length;
            byte* native = (byte*)NativeMemory.Alloc((nuint)size);
            try
            {
                int read = stream.ReadAtLeast(new Span<byte>(native, size), size, throwOnEndOfStream: false);
                if (read == size && stream.ReadByte() < 0)
                    return new NativeFontBuffer((nint)native, size);

                // Size shifted mid-read; fall back to full read
                NativeMemory.Free(native);
            }
            catch
            {
                NativeMemory.Free(native);
                throw;
            }
        }

        byte[] fallback = ContentFiles.ReadAllBytes(path);
        byte* copy = (byte*)NativeMemory.Alloc((nuint)fallback.Length);
        fallback.CopyTo(new Span<byte>(copy, fallback.Length));
        return new NativeFontBuffer((nint)copy, fallback.Length);
    }

    // Helper method to build a canonical deduplication key for a font file's native bytes
    private static string BufferKey(string path, string cacheKey)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
                return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return cacheKey;
    }

    /// <summary>Returns already-loaded outlines for <paramref name="path"/> without triggering a load.</summary>
    internal static bool TryGetLoaded(string path, out GlyphOutlines? outlines)
    {
        if (known.TryGetValue(ContentFiles.CacheKey(path), out Lazy<GlyphOutlines?>? lazy) && lazy.IsValueCreated)
        {
            outlines = lazy.Value;
            return outlines is not null;
        }

        outlines = null;
        return false;
    }

    /// <summary>
    /// Resolves the byte offset of a face's sfnt table directory inside a single TrueType/OpenType font or a TrueType
    /// Collection (<c>.ttc</c>, starting with <c>'ttcf'</c>). Per the OpenType TTC spec, table record offsets inside the
    /// directory at the returned offset remain relative to byte 0 of the collection file.
    /// </summary>
    internal static bool TryGetFontOffset(ReadOnlySpan<byte> bytes, int faceIndex, out int offset)
    {
        offset = 0;
        if (faceIndex < 0 || bytes.Length < 12)
            return false;

        uint tag = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (tag == TTC_TAG)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(8, 4));
            if ((uint)faceIndex >= count)
                return false;

            long entryOffset = 12L + (long)faceIndex * 4L;
            if (entryOffset + 4 > bytes.Length)
                return false;

            uint fontOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice((int)entryOffset, 4));
            if (fontOffset > int.MaxValue || (long)fontOffset + 12 > bytes.Length)
                return false;

            offset = (int)fontOffset;
            return true;
        }

        return faceIndex == 0;
    }

    /// <summary>The outlines of a TrueType font file (<c>.ttf</c>, <c>.otf</c>, <c>.ttc</c>), null for anything else or a file that won't read.</summary>
    public static GlyphOutlines? For(string path)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path.AsSpan());
        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
            return null;

        // A file that isn't there yet is looked for again next time rather than remembered as missing
        if (!ContentFiles.Exists(path))
            return null;

        string cacheKey = ContentFiles.CacheKey(path);
        return known.GetOrAdd(cacheKey, _ => new Lazy<GlyphOutlines?>(() =>
        {
            try
            {
                string bufKey = BufferKey(path, cacheKey);
                NativeFontBuffer buffer = sharedBuffers.GetOrAdd(bufKey, _ => new Lazy<NativeFontBuffer>(() => LoadNativeBuffer(path))).Value;
                return new GlyphOutlines((byte*)buffer.Pointer, buffer.Length, ownsMemory: false);
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
    /// Fills a letter in, as many pixels of coverage as <paramref name="box"/> says, a row after another, one byte each.
    /// Not safe on two threads at once, the glyph atlas calls it under its lock. The pixels go into
    /// <paramref name="into"/>, which is made bigger when it has to be.
    /// </summary>
    /// <param name="pixelHeight">How tall the font is drawn, ascent to descent, in pixels of the screen.</param>
    /// <param name="shift">How far into its first pixel the pen is, 0 to 1.</param>
    /// <param name="bold">How many pixels further across the ink is smeared, 0 for none.</param>
    /// <param name="slant">How far the letter leans across for every unit up, 0 for upright.</param>
    /// <param name="hint">Whether the x height goes on a whole row of pixels. Off on a Retina screen, where a row is
    /// so small that moving the letters to fit only changes their shape.</param>
    /// <returns>Whether there is anything to draw.</returns>
    public bool Rasterize(char character, float pixelHeight, float shift, float bold, float slant, bool hint, ref byte[] into, out GlyphBox box)
    {
        box = default;
        if (!Fill(character, pixelHeight, shift, bold, slant, hint, 1, ref into, out int left, out int top, out int width, out int height))
            return false;
        box = new GlyphBox(left, top, width, height);
        return true;
    }

    // FreeType's default LCD filter, how much of each of five subpixels around one goes into it, out of 256. Without it
    // every edge has a red or blue fringe a whole subpixel wide, with it the colour is spread so it reads as a sharper grey
    private static ReadOnlySpan<byte> LcdFilter => [8, 77, 86, 77, 8];

    /// <summary>
    /// Fills a letter in for subpixel drawing, its coverage worked out at three times the width and each pixel's red,
    /// green and blue stripes given their own, four bytes a pixel (red, green, blue, and the most of them as alpha).
    /// The stripes are in the screen's order, <paramref name="bgr"/> for a panel whose blue is on the left.
    /// </summary>
    /// <param name="work">Room for the coverage at three times the width, made bigger when it has to be.</param>
    public bool RasterizeSubpixel(char character, float pixelHeight, float shift, float bold, float slant, bool hint, bool bgr,
        ref byte[] work, ref byte[] into, out GlyphBox box)
    {
        box = default;
        if (!Fill(character, pixelHeight, shift, bold, slant, hint, 3, ref work, out int left, out int top, out int across, out int height))
            return false;

        int width = across / 3;
        int size = width * height * 4;
        if (into.Length < size)
            into = new byte[Math.Max(size, into.Length * 2)];

        ReadOnlySpan<byte> filter = LcdFilter;
        int f0 = filter[0], f1 = filter[1], f2 = filter[2], f3 = filter[3], f4 = filter[4];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = work.AsSpan(y * across, across);
            Span<byte> pixels = into.AsSpan(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                int at = x * 3, r, g, b;
                if (x > 0 && x < width - 1)
                {
                    // Away from the ends all seven subpixels the three stripes reach are in the row, read once each
                    // and with no end of the row to look out for, which halves what filtering a letter costs
                    ReadOnlySpan<byte> taps = row.Slice(at - 2, 7);
                    int t0 = taps[0], t1 = taps[1], t2 = taps[2], t3 = taps[3], t4 = taps[4], t5 = taps[5], t6 = taps[6];
                    r = f0 * t0 + f1 * t1 + f2 * t2 + f3 * t3 + f4 * t4;
                    g = f0 * t1 + f1 * t2 + f2 * t3 + f3 * t4 + f4 * t5;
                    b = f0 * t2 + f1 * t3 + f2 * t4 + f3 * t5 + f4 * t6;
                }
                else
                {
                    r = g = b = 0;
                    for (int tap = 0; tap < 5; tap++)
                    {
                        int weight = filter[tap];
                        r += weight * At(row, at + tap - 2);
                        g += weight * At(row, at + 1 + tap - 2);
                        b += weight * At(row, at + 2 + tap - 2);
                    }
                }

                byte first = (byte)Math.Min(255, r >> 8), second = (byte)Math.Min(255, g >> 8), third = (byte)Math.Min(255, b >> 8);
                Span<byte> pixel = pixels.Slice(x * 4, 4);
                pixel[3] = Math.Max(first, Math.Max(second, third));
                pixel[0] = bgr ? third : first;
                pixel[1] = second;
                pixel[2] = bgr ? first : third;
            }
        }

        box = new GlyphBox(left / 3, top, width, height);
        return true;

        static int At(ReadOnlySpan<byte> row, int at) => (uint)at < (uint)row.Length ? row[at] : 0;
    }

    // The outline filled in at so many samples across a pixel (1, or 3 for subpixels), the box in those samples across
    // and pixels down. Three across is padded by the filter's reach and put on whole pixels, so the stripes line up
    private bool Fill(char character, float pixelHeight, float shift, float bold, float slant, bool hint, int across,
        ref byte[] into, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
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
            float scaleY = hint ? scale * Hinted(scale) : scale;
            float scaleX = scale * across;

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

                Grow(ref minX, ref minY, ref maxX, ref maxY, v.x, v.y);
                if (v.type is 3 or 4)
                    Grow(ref minX, ref minY, ref maxX, ref maxY, v.cx, v.cy);
                if (v.type == 4)
                    Grow(ref minX, ref minY, ref maxX, ref maxY, v.cx1, v.cy1);
            }

            if (minX > maxX || minY > maxY)
                return false;

            float shifted = shift * across;
            int pad = across == 1 ? 0 : 2;
            left = (int)MathF.Floor(minX * scaleX + shifted) - pad;
            int right = (int)MathF.Ceiling(maxX * scaleX + shifted + bold * across) + pad;
            if (across > 1)
            {
                left = (int)MathF.Floor(left / (float)across) * across;
                right = (int)MathF.Ceiling(right / (float)across) * across;
            }
            top = (int)MathF.Floor(-maxY * scaleY);
            int bottom = (int)MathF.Ceiling(-minY * scaleY);
            width = right - left;
            height = bottom - top;
            if (width <= 0 || height <= 0)
                return false;

            int size = width * height;
            if (into.Length < size)
                into = new byte[Math.Max(size, into.Length * 2)];
            Array.Clear(into, 0, size);

            fixed (byte* pixels = into)
            {
                var bitmap = new StbTrueType.stbtt__bitmap { w = width, h = height, stride = width, pixels = pixels };
                StbTrueType.stbtt_Rasterize(&bitmap, 0.35f, vertices, count, scaleX, scaleY, shifted, 0.0f, left, top, 1, null, false);
            }

            if (bold > 0.0f)
                Embolden(into.AsSpan(0, size), width, height, bold * across);
            return true;
        }
        finally
        {
            StbTrueType.stbtt_FreeShape(info, vertices);
        }

        static void Grow(ref float minX, ref float minY, ref float maxX, ref float maxY, float x, float y)
        {
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
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
