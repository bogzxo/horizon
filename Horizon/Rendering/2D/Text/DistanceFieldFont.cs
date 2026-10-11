using System.Numerics;
using System.Security.Cryptography;

using Horizon.Graphics;
using Horizon.Logging;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using StbTrueTypeSharp;

namespace Horizon.Rendering.Text;

/// <summary>
/// A font as a signed distance field. Every glyph is kept in one R8 atlas not as its picture but as how far every
/// texel is from the edge of its ink (128 on the edge, brighter inside, darker outside, a few texels of reach either
/// way), which the sprite shader turns back into a crisp edge at whatever size the text is drawn, big or small,
/// with no mipmaps and no blur. Bitmap fonts could only be drawn smaller than they were baked, and got soft doing it.
/// <para>
/// Made out of a TrueType file (<c>.ttf</c>, <c>.otf</c>), the glyphs rasterised straight into distances by
/// stb_truetype at <see cref="FIELD_EM"/> pixels to the em and packed on shelves, with the kerning the font has.
/// Or out of a BMFont bitmap font (<c>.fnt</c> and its picture), the coverage of the picture turned into
/// distances by an exact distance transform, so the pixel fonts the skins were made with carry on as they are.
/// Either way the metrics come out at <see cref="EmSize"/> pixels to the em, the size the bitmap fonts were baked
/// at, so a skin's text_scale means what it always did. What was built is kept on disk next to the shader cache,
/// by a hash of the file, so it is only built once.
/// </para>
/// </summary>
public sealed class DistanceFieldFont
{
    /// <summary>The size the metrics are given at unless asked otherwise, which is what the bitmap fonts were baked at.</summary>
    public const int DEFAULT_EM = 96;

    /// <summary>How big a TrueType font's field is drawn, pixels to the em. Plenty, the field scales.</summary>
    public const int FIELD_EM = 48;

    /// <summary>How far the field reaches either side of an edge, in texels of the field.</summary>
    public const int SPREAD = 6;

    private const int CACHE_VERSION = 1;
    private const uint CACHE_MAGIC = 0x544E4648; // HFNT

    // The characters a TrueType font is built with, the printable ASCII and the Latin-1 ones
    private static readonly (int From, int To)[] Ranges = [(32, 126), (160, 255)];

    public Texture Texture { get; private set; } = Texture.Invalid;
    public Dictionary<char, CharDefinition> Glyphs { get; } = [];

    /// <summary>Kerning between two characters (the first in the high half of the key), in pixels at the em size. Empty for most fonts.</summary>
    public Dictionary<int, float> Kerning { get; } = [];

    /// <summary>The height of a line and how far down it the baseline is, in pixels at the em size.</summary>
    public float LineHeight { get; private set; }
    public float Base { get; private set; }

    /// <summary>How many pixels to the em the metrics are given at.</summary>
    public int EmSize { get; private set; }

    /// <summary>How far the field reaches either side of an edge, in pixels at the em size. For outlines and shadows.</summary>
    public float Spread { get; private set; }

    public static int KerningKey(char first, char second) => (first << 16) | second;

    /// <summary>
    /// Loads a font, TrueType or BMFont by its extension. Render thread (it makes the texture).
    /// </summary>
    /// <param name="emSize">How many pixels to the em the metrics come out at.</param>
    public static DistanceFieldFont Load(string directory, string file, int emSize = DEFAULT_EM)
    {
        string path = Path.Combine(directory, file);
        var font = new DistanceFieldFont { EmSize = Math.Max(8, emSize) };

        try
        {
            if (!File.Exists(path))
            {
                Log.Error($"[DistanceFieldFont] There is no font at '{path}'.");
                return font;
            }

            if (font.TryLoadCached(path)) return font;

            bool trueType = Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf";
            (int width, int height, byte[] field) = trueType ? font.BuildFromTrueType(path) : font.BuildFromBitmap(directory, path);
            if (field.Length == 0) return font;

            font.Texture = Texture.FromPixels((uint)width, (uint)height, field, new TextureDefinition(PixelFormat.R8, Smooth: true));
            font.KeepCached(path, width, height, field);
        }
        catch (Exception e)
        {
            Log.Error($"[DistanceFieldFont] '{path}' would not load, {e.Message}");
        }

        return font;
    }

    /* TrueType, through stb_truetype */

    private unsafe (int, int, byte[]) BuildFromTrueType(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var info = new StbTrueType.stbtt_fontinfo();
        fixed (byte* data = bytes)
        {
            if (StbTrueType.stbtt_InitFont(info, data, StbTrueType.stbtt_GetFontOffsetForIndex(data, 0)) == 0)
                throw new InvalidDataException("not a TrueType font stb_truetype can read");

            float fieldScale = StbTrueType.stbtt_ScaleForPixelHeight(info, FIELD_EM);
            float toEm = (float)EmSize / FIELD_EM;

            int ascent, descent, lineGap;
            StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
            LineHeight = MathF.Round((ascent - descent + lineGap) * fieldScale * toEm);
            Base = MathF.Round(ascent * fieldScale * toEm);
            Spread = SPREAD * toEm;

            // Every glyph's field, and the shelves they go on
            var pictures = new List<(char Id, int Width, int Height, int XOff, int YOff, float Advance, byte[]? Field)>();
            foreach (var (from, to) in Ranges)
            {
                for (int codepoint = from; codepoint <= to; codepoint++)
                {
                    if (StbTrueType.stbtt_FindGlyphIndex(info, codepoint) == 0 && codepoint != ' ') continue;

                    int advance, bearing;
                    StbTrueType.stbtt_GetCodepointHMetrics(info, codepoint, &advance, &bearing);

                    int width = 0, height = 0, xOff = 0, yOff = 0;
                    byte* sdf = StbTrueType.stbtt_GetCodepointSDF(info, fieldScale, codepoint, SPREAD, 128, 128.0f / SPREAD, &width, &height, &xOff, &yOff);
                    byte[]? field = null;
                    if (sdf != null && width > 0 && height > 0)
                    {
                        field = new byte[width * height];
                        new ReadOnlySpan<byte>(sdf, field.Length).CopyTo(field);
                        StbTrueType.stbtt_FreeSDF(sdf, null);
                    }

                    pictures.Add(((char)codepoint, width, height, xOff, yOff, advance * fieldScale, field));
                }
            }

            // Kerning, for the pairs the font has
            foreach (var a in pictures)
            {
                foreach (var b in pictures)
                {
                    int kern = StbTrueType.stbtt_GetCodepointKernAdvance(info, a.Id, b.Id);
                    if (kern != 0) Kerning[KerningKey(a.Id, b.Id)] = kern * fieldScale * toEm;
                }
            }

            // Shelves, tallest first, in a square that grows until everything fits
            pictures.Sort((p, q) => q.Height.CompareTo(p.Height));
            int side = 256;
            while (!Fits(pictures, side, side)) side *= 2;
            int atlasHeight = Pack(pictures, side, out var placed);
            atlasHeight = Math.Max(1, (int)BitOperations.RoundUpToPowerOf2((uint)atlasHeight));

            var atlas = new byte[side * atlasHeight];
            foreach (var (picture, x, y) in placed)
            {
                if (picture.Field is { } field)
                {
                    for (int row = 0; row < picture.Height; row++)
                        Array.Copy(field, row * picture.Width, atlas, (y + row) * side + x, picture.Width);
                }

                // The top of the line is the ascent above the baseline, the picture's top is yOff below the baseline (up is negative)
                Glyphs[picture.Id] = new CharDefinition
                {
                    Id = picture.Id,
                    Position = new Vector2(x, y),
                    TexelSize = new Vector2(picture.Width, picture.Height),
                    Size = new Vector2(picture.Width, picture.Height) * toEm,
                    Offset = new Vector2(picture.XOff, ascent * fieldScale + picture.YOff) * toEm,
                    XAdvance = picture.Advance * toEm
                };
            }

            return (side, atlasHeight, atlas);
        }
    }

    private static bool Fits(List<(char Id, int Width, int Height, int XOff, int YOff, float Advance, byte[]? Field)> pictures, int width, int height)
    {
        return Pack(pictures, width, out _) <= height;
    }

    /// <summary>Helper method to lay the glyphs out on shelves across a width, a texel between them. How tall it came to.</summary>
    private static int Pack(List<(char Id, int Width, int Height, int XOff, int YOff, float Advance, byte[]? Field)> pictures, int width, out List<((char Id, int Width, int Height, int XOff, int YOff, float Advance, byte[]? Field) Picture, int X, int Y)> placed)
    {
        placed = [];
        int x = 1, y = 1, shelf = 0;
        foreach (var picture in pictures)
        {
            if (x + picture.Width + 1 > width)
            {
                x = 1;
                y += shelf + 1;
                shelf = 0;
            }

            placed.Add((picture, x, y));
            x += picture.Width + 1;
            shelf = Math.Max(shelf, picture.Height);
        }

        return y + shelf + 1;
    }

    /* BMFont, the picture turned into distances */

    private (int, int, byte[]) BuildFromBitmap(string directory, string path)
    {
        var importer = new BMFontImporter(directory, Path.GetFileName(path));
        if (importer.ImagePath.Length == 0 || !File.Exists(importer.ImagePath))
            throw new FileNotFoundException($"the picture of the font, '{importer.ImagePath}', isn't there");

        using var image = Image.Load<Rgba32>(importer.ImagePath);
        int width = image.Width, height = image.Height;
        var inside = new bool[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++) inside[y * width + x] = row[x].A > 127;
            }
        });

        // The field at the picture's own size, then halved if it's a big one, distances survive that fine
        int spread = Math.Max(2, importer.LineHeight > 0 ? importer.LineHeight / 12 : SPREAD);
        byte[] field = SignedField(inside, width, height, spread);
        int shrink = width > 1024 ? 2 : 1;
        if (shrink == 2)
        {
            (width, height, field) = Halve(field, width, height);
            spread /= 2;
        }

        float tallest = 0.0f;
        foreach (var glyph in importer.Definitions.Values)
        {
            Glyphs[glyph.Id] = new CharDefinition
            {
                Id = glyph.Id,
                Position = glyph.Position / shrink,
                TexelSize = glyph.TexelSize / shrink,
                Size = glyph.Size,
                Offset = glyph.Offset,
                XAdvance = glyph.XAdvance
            };
            tallest = MathF.Max(tallest, glyph.Offset.Y + glyph.Size.Y);
        }

        LineHeight = importer.LineHeight > 0 ? importer.LineHeight : tallest;
        Base = importer.Base > 0 ? importer.Base : LineHeight;
        Spread = spread * shrink;

        // A bitmap font's metrics are its own, they were baked at whatever size they were baked at
        EmSize = DEFAULT_EM;
        return (width, height, field);
    }

    /// <summary>
    /// Helper method for the signed distance of every texel to the edge of the ink, 128 on the edge, up inside
    /// and down outside, by spread texels to the ends. An exact transform, Felzenszwalb and Huttenlocher's, over
    /// the columns and then the rows, once for the distance to the ink and once for the distance out of it.
    /// </summary>
    private static byte[] SignedField(bool[] inside, int width, int height, int spread)
    {
        float[] toInk = Transform(inside, width, height, true);
        float[] toAir = Transform(inside, width, height, false);

        var field = new byte[width * height];
        for (int i = 0; i < field.Length; i++)
        {
            // The edge is half a texel past the texels on it either way
            float distance = inside[i] ? -(MathF.Sqrt(toAir[i]) - 0.5f) : MathF.Sqrt(toInk[i]) - 0.5f;
            field[i] = (byte)Math.Clamp(MathF.Round(128.0f - distance * 128.0f / spread), 0.0f, 255.0f);
        }

        return field;
    }

    /// <summary>Helper method for the squared distance of every texel to the nearest texel that is (or isn't) inside.</summary>
    private static float[] Transform(bool[] inside, int width, int height, bool toInside)
    {
        const float Far = 1e12f;
        var f = new float[width * height];
        for (int i = 0; i < f.Length; i++) f[i] = inside[i] == toInside ? 0.0f : Far;

        int longest = Math.Max(width, height);
        var line = new float[longest];
        var d = new float[longest];
        var v = new int[longest];
        var z = new float[longest + 1];

        // Down every column
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++) line[y] = f[y * width + x];
            Envelope(line, height, d, v, z);
            for (int y = 0; y < height; y++) f[y * width + x] = d[y];
        }

        // Then along every row
        for (int y = 0; y < height; y++)
        {
            Array.Copy(f, y * width, line, 0, width);
            Envelope(line, width, d, v, z);
            Array.Copy(d, 0, f, y * width, width);
        }

        return f;
    }

    /// <summary>Helper method for the one dimensional transform, the lower envelope of the parabolas over a line.</summary>
    private static void Envelope(float[] f, int n, float[] d, int[] v, float[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int p = v[k];
                s = ((f[q] + (float)q * q) - (f[p] + (float)p * p)) / (2.0f * (q - p));
                if (s > z[k] || k == 0) break;
                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            float dx = q - v[k];
            d[q] = dx * dx + f[v[k]];
        }
    }

    private static (int, int, byte[]) Halve(byte[] field, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var small = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int a = field[(2 * y) * width + 2 * x], b = field[(2 * y) * width + Math.Min(2 * x + 1, width - 1)];
                int c = field[Math.Min(2 * y + 1, height - 1) * width + 2 * x], e = field[Math.Min(2 * y + 1, height - 1) * width + Math.Min(2 * x + 1, width - 1)];
                small[y * w + x] = (byte)((a + b + c + e + 2) / 4);
            }
        }

        return (w, h, small);
    }

    /* The cache, next to the shaders' */

    private static string? CacheFile(string path, int emSize)
    {
        string? shaders = ShaderCompiler.CacheDirectory();
        if (shaders is null) return null;

        var hash = SHA256.HashData(File.ReadAllBytes(path));
        string key = Convert.ToHexString(hash)[..24] + "_" + emSize + "_" + CACHE_VERSION;
        return Path.Combine(Path.GetDirectoryName(shaders) ?? shaders, "Fonts", key + ".hfont");
    }

    private bool TryLoadCached(string path)
    {
        string? file = CacheFile(path, EmSize);
        if (file is null || !File.Exists(file)) return false;

        try
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadUInt32() != CACHE_MAGIC) return false;

            int width = reader.ReadInt32(), height = reader.ReadInt32();
            LineHeight = reader.ReadSingle();
            Base = reader.ReadSingle();
            Spread = reader.ReadSingle();
            EmSize = reader.ReadInt32();

            int glyphs = reader.ReadInt32();
            for (int i = 0; i < glyphs; i++)
            {
                var glyph = new CharDefinition
                {
                    Id = (char)reader.ReadUInt16(),
                    Position = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    TexelSize = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    Size = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    Offset = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    XAdvance = reader.ReadSingle()
                };
                Glyphs[glyph.Id] = glyph;
            }

            int pairs = reader.ReadInt32();
            for (int i = 0; i < pairs; i++) Kerning[reader.ReadInt32()] = reader.ReadSingle();

            byte[] field = reader.ReadBytes(width * height);
            if (field.Length != width * height) return false;

            Texture = Texture.FromPixels((uint)width, (uint)height, field, new TextureDefinition(PixelFormat.R8, Smooth: true));
            return true;
        }
        catch (Exception)
        {
            // A cache that doesn't read is a cache that gets made again
            Glyphs.Clear();
            Kerning.Clear();
            return false;
        }
    }

    private void KeepCached(string path, int width, int height, byte[] field)
    {
        string? file = CacheFile(path, EmSize);
        if (file is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var writer = new BinaryWriter(File.Create(file));
            writer.Write(CACHE_MAGIC);
            writer.Write(width);
            writer.Write(height);
            writer.Write(LineHeight);
            writer.Write(Base);
            writer.Write(Spread);
            writer.Write(EmSize);
            writer.Write(Glyphs.Count);
            foreach (var glyph in Glyphs.Values)
            {
                writer.Write((ushort)glyph.Id);
                writer.Write(glyph.Position.X); writer.Write(glyph.Position.Y);
                writer.Write(glyph.TexelSize.X); writer.Write(glyph.TexelSize.Y);
                writer.Write(glyph.Size.X); writer.Write(glyph.Size.Y);
                writer.Write(glyph.Offset.X); writer.Write(glyph.Offset.Y);
                writer.Write(glyph.XAdvance);
            }
            writer.Write(Kerning.Count);
            foreach (var (key, value) in Kerning)
            {
                writer.Write(key);
                writer.Write(value);
            }
            writer.Write(field);
        }
        catch (Exception e)
        {
            Log.Warning($"[DistanceFieldFont] Couldn't keep the font at '{file}', {e.Message}");
        }
    }
}
