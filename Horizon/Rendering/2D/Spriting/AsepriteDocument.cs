using System.Buffers.Binary;
using System.IO.Compression;

using Horizon.Logging;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// One animation of an Aseprite file, a tag in Aseprite's words. Which frames it covers and which way it plays.
/// </summary>
/// <param name="Name">What the tag is called, which is what the animation goes by.</param>
/// <param name="From">The first frame of it, counted from the start of the file.</param>
/// <param name="To">The last frame of it, included.</param>
/// <param name="Direction">0 forward, 1 reverse, 2 ping pong, 3 ping pong reverse, as Aseprite writes them.</param>
public readonly record struct AsepriteTag(string Name, int From, int To, int Direction)
{
    public int Frames => To - From + 1;
}

/// <summary>
/// An Aseprite file as the game reads it, no exporting a sheet first. The frames are put together out of their
/// layers when they're asked for (and kept, so the atlas and the box tracer don't both pay for it) and every tag is
/// an animation. Read once per file, see <see cref="Open"/> and <see cref="Forget"/>.
/// <para>
/// What's read is RGBA, grayscale and indexed files, any number of layers (hidden ones left out, groups honoured),
/// raw, linked and zlib compressed cels, layer and cel opacity, tags. What isn't is blend modes other than normal
/// (they're drawn as normal and said so once), tilemap layers, external files. Enough for sprites drawn in it.
/// </para>
/// </summary>
public sealed class AsepriteDocument
{
    private const ushort FILE_MAGIC = 0xA5E0;
    private const ushort FRAME_MAGIC = 0xF1FA;

    private const ushort CHUNK_OLD_PALETTE = 0x0004;
    private const ushort CHUNK_OLD_PALETTE_2 = 0x0011;
    private const ushort CHUNK_LAYER = 0x2004;
    private const ushort CHUNK_CEL = 0x2005;
    private const ushort CHUNK_TAGS = 0x2018;
    private const ushort CHUNK_PALETTE = 0x2019;

    private const ushort LAYER_VISIBLE = 1;
    private const ushort LAYER_GROUP = 1;
    private const ushort LAYER_TILEMAP = 2;

    private const ushort CEL_RAW = 0;
    private const ushort CEL_LINKED = 1;
    private const ushort CEL_COMPRESSED = 2;
    private const ushort CEL_TILEMAP = 3;

    private const int BLEND_NORMAL = 0;

    private static readonly Dictionary<string, AsepriteDocument> cache = [];

    private readonly record struct Layer(string Name, bool Visible, bool Group, bool Tilemap, int Depth, int Blend, byte Opacity);

    // Where a cel's pixels are in the file (or which frame they're linked to), for putting the frame together later
    private readonly record struct Cel(int Layer, int X, int Y, int Width, int Height, byte Opacity, ushort Kind, int DataOffset, int DataLength, int LinkedFrame);

    private readonly byte[] file;
    private readonly int depth;
    private readonly byte transparentIndex;
    private readonly uint[] palette = new uint[256];
    private readonly List<Layer> layers = [];
    private readonly List<Cel>[] cels;
    private readonly int[] durations;
    private readonly AsepriteTag[] tags;
    private readonly byte[]?[] rendered;
    private bool warnedBlend;

    /// <summary>Where it was read from.</summary>
    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>How many frames there are in the file, every tag's included.</summary>
    public int FrameCount => durations.Length;

    /// <summary>The animations, in the order they're in the file.</summary>
    public IReadOnlyList<AsepriteTag> Tags => tags;

    /// <summary>Whether a path is one of ours by its extension.</summary>
    public static bool IsAseprite(string path) =>
        path.EndsWith(".ase", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".aseprite", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a file, or hands back the one read before. Throws, saying what's wrong, for a file that isn't an Aseprite file.
    /// </summary>
    public static AsepriteDocument Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);

        lock (cache)
        {
            if (cache.TryGetValue(full, out var known)) return known;

            var document = new AsepriteDocument(full, File.ReadAllBytes(full));
            cache[full] = document;
            return document;
        }
    }

    /// <summary>
    /// Forgets every file that was read, for when they've changed on disk. Whoever opens one next reads it again.
    /// </summary>
    public static void Forget()
    {
        lock (cache) cache.Clear();
    }

    /// <summary>Finds an animation by name.</summary>
    public bool TryGetTag(string name, out AsepriteTag tag)
    {
        foreach (var candidate in tags)
        {
            if (candidate.Name == name)
            {
                tag = candidate;
                return true;
            }
        }

        tag = default;
        return false;
    }

    /// <summary>How long a frame is shown for, in seconds.</summary>
    public float Duration(int frame) => durations[Math.Clamp(frame, 0, durations.Length - 1)] / 1000.0f;

    /// <summary>
    /// A frame as it looks with its layers on top of each other, RGBA from the top left, <see cref="Width"/> by
    /// <see cref="Height"/>. Put together the first time, kept after. Not to be written to.
    /// </summary>
    public byte[] ReadFrame(int frame)
    {
        if ((uint)frame >= (uint)FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frame), $"'{Path}' has {FrameCount} frames, there is no frame {frame}.");

        lock (rendered)
        {
            return rendered[frame] ??= Render(frame);
        }
    }

    private AsepriteDocument(string path, byte[] file)
    {
        Path = path;
        this.file = file;

        if (file.Length < 128 || U16(4) != FILE_MAGIC)
            throw new InvalidDataException($"'{path}' is not an Aseprite file.");

        int frameCount = U16(6);
        Width = U16(8);
        Height = U16(10);
        depth = U16(12);
        transparentIndex = file[28];

        if (depth is not (32 or 16 or 8))
            throw new InvalidDataException($"'{path}' is {depth} bits a pixel, which isn't a thing.");
        if (Width < 1 || Height < 1 || frameCount < 1)
            throw new InvalidDataException($"'{path}' has nothing in it.");

        durations = new int[frameCount];
        cels = new List<Cel>[frameCount];
        rendered = new byte[frameCount][];

        var foundTags = new List<AsepriteTag>();

        int offset = 128;
        for (int frame = 0; frame < frameCount; frame++)
        {
            if (offset + 16 > file.Length)
                throw new InvalidDataException($"'{path}' stops short in frame {frame}.");

            int frameBytes = (int)U32(offset);
            if (U16(offset + 4) != FRAME_MAGIC)
                throw new InvalidDataException($"'{path}' has a frame {frame} that isn't one.");

            int chunks = U16(offset + 6);
            durations[frame] = U16(offset + 8);
            int newChunks = (int)U32(offset + 12);
            if (newChunks != 0) chunks = newChunks;

            cels[frame] = [];

            int at = offset + 16;
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                if (at + 6 > file.Length)
                    throw new InvalidDataException($"'{path}' stops short in a chunk of frame {frame}.");

                int chunkBytes = (int)U32(at);
                ushort kind = U16(at + 4);
                int body = at + 6;

                switch (kind)
                {
                    case CHUNK_LAYER:
                        ReadLayer(body);
                        break;

                    case CHUNK_CEL:
                        ReadCel(frame, body, at + chunkBytes);
                        break;

                    case CHUNK_TAGS:
                        ReadTags(body, foundTags);
                        break;

                    case CHUNK_PALETTE:
                        ReadPalette(body);
                        break;

                    case CHUNK_OLD_PALETTE:
                    case CHUNK_OLD_PALETTE_2:
                        ReadOldPalette(body);
                        break;
                }

                at += Math.Max(chunkBytes, 6);
            }

            offset += Math.Max(frameBytes, 16);
        }

        tags = [.. foundTags];
    }

    private void ReadLayer(int at)
    {
        ushort flags = U16(at);
        ushort type = U16(at + 2);
        int depth = U16(at + 4);
        ushort blend = U16(at + 10);
        byte opacity = file[at + 12];
        int nameLength = U16(at + 16);
        string name = System.Text.Encoding.UTF8.GetString(file, at + 18, nameLength);

        // A layer inside a hidden group is hidden with it, however it's flagged itself
        bool visible = (flags & LAYER_VISIBLE) != 0;
        for (int i = layers.Count - 1; i >= 0 && visible; i--)
        {
            if (layers[i].Depth < depth && layers[i].Group)
            {
                visible = layers[i].Visible;
                depth = layers[i].Depth;
            }
        }

        layers.Add(new Layer(name, visible, type == LAYER_GROUP, type == LAYER_TILEMAP, U16(at + 4), blend, opacity));
    }

    private void ReadCel(int frame, int at, int end)
    {
        int layer = U16(at);
        int x = (short)U16(at + 2);
        int y = (short)U16(at + 4);
        byte opacity = file[at + 6];
        ushort kind = U16(at + 7);
        int body = at + 16;

        switch (kind)
        {
            case CEL_RAW:
            case CEL_COMPRESSED:
                {
                    int width = U16(body), height = U16(body + 2);
                    cels[frame].Add(new Cel(layer, x, y, width, height, opacity, kind, body + 4, end - (body + 4), -1));
                    break;
                }

            case CEL_LINKED:
                cels[frame].Add(new Cel(layer, x, y, 0, 0, opacity, kind, 0, 0, U16(body)));
                break;

            case CEL_TILEMAP:
                // Not drawn, a tilemap layer wants its tileset and that's a job for another day
                break;
        }
    }

    private void ReadTags(int at, List<AsepriteTag> into)
    {
        int count = U16(at);
        int p = at + 10;
        for (int i = 0; i < count; i++)
        {
            int from = U16(p), to = U16(p + 2);
            int direction = file[p + 4];
            int nameLength = U16(p + 17);
            string name = System.Text.Encoding.UTF8.GetString(file, p + 19, nameLength);
            into.Add(new AsepriteTag(name, Math.Clamp(from, 0, FrameCount - 1), Math.Clamp(to, 0, FrameCount - 1), direction));
            p += 19 + nameLength;
        }
    }

    private void ReadPalette(int at)
    {
        uint size = U32(at);
        int first = (int)U32(at + 4), last = (int)U32(at + 8);
        int p = at + 20;
        for (int i = first; i <= last && i < 256 && i < size; i++)
        {
            ushort flags = U16(p);
            byte r = file[p + 2], g = file[p + 3], b = file[p + 4], a = file[p + 5];
            palette[i] = Pack(r, g, b, a);
            p += 6;
            if ((flags & 1) != 0) p += 2 + U16(p);
        }
    }

    private void ReadOldPalette(int at)
    {
        int packets = U16(at);
        int p = at + 2, index = 0;
        for (int i = 0; i < packets; i++)
        {
            index += file[p];
            int count = file[p + 1] == 0 ? 256 : file[p + 1];
            p += 2;
            for (int j = 0; j < count && index + j < 256; j++, p += 3)
                palette[index + j] = Pack(file[p], file[p + 1], file[p + 2], 255);
            index += count;
        }
    }

    private byte[] Render(int frame)
    {
        var pixels = new byte[Width * Height * 4];

        // Bottom layer first, each cel blended over what's there
        foreach (var cel in CelsInOrder(frame))
        {
            var layer = cel.Layer < layers.Count ? layers[cel.Layer] : default;
            if (!layer.Visible || layer.Group || layer.Tilemap) continue;

            Cel source = cel;
            if (cel.Kind == CEL_LINKED)
            {
                source = default;
                foreach (var candidate in cels[Math.Clamp(cel.LinkedFrame, 0, FrameCount - 1)])
                    if (candidate.Layer == cel.Layer && candidate.Kind != CEL_LINKED) { source = candidate; break; }
                if (source.Width == 0) continue;
                source = source with { X = source.X, Y = source.Y, Opacity = cel.Opacity };
            }

            if (layer.Blend != BLEND_NORMAL && !warnedBlend)
            {
                Log.Warning($"[Aseprite] '{Path}' blends layer '{layer.Name}' in a mode the engine draws as normal.");
                warnedBlend = true;
            }

            byte[] celPixels = Decode(source);
            int alpha = source.Opacity * layer.Opacity / 255;
            Composite(pixels, celPixels, source, alpha);
        }

        return pixels;
    }

    private IEnumerable<Cel> CelsInOrder(int frame)
    {
        // Layers are written bottom to top, which is the order to draw them in
        var list = cels[frame];
        for (int i = 0; i < list.Count; i++)
        {
            int lowest = -1;
            for (int j = 0; j < list.Count; j++)
            {
                if (list[j].Layer < i) continue;
                if (list[j].Layer != i) continue;
                lowest = j;
                break;
            }

            if (lowest >= 0) yield return list[lowest];
        }

        // Cels on layers past the count above (a file with more layers than cels in a frame) come after, in file order
        for (int j = 0; j < list.Count; j++)
            if (list[j].Layer >= list.Count) yield return list[j];
    }

    private byte[] Decode(in Cel cel)
    {
        int bytesPerPixel = depth / 8;
        int wanted = cel.Width * cel.Height * bytesPerPixel;
        var raw = new byte[wanted];

        if (cel.Kind == CEL_COMPRESSED)
        {
            using var stream = new MemoryStream(file, cel.DataOffset, cel.DataLength, false);
            using var inflate = new ZLibStream(stream, CompressionMode.Decompress);
            int read = 0;
            while (read < wanted)
            {
                int got = inflate.Read(raw, read, wanted - read);
                if (got <= 0) break;
                read += got;
            }
        }
        else
        {
            Array.Copy(file, cel.DataOffset, raw, 0, Math.Min(wanted, cel.DataLength));
        }

        if (depth == 32) return raw;

        // Grayscale and indexed are turned into RGBA here, so the rest only ever sees one kind of pixel
        var rgba = new byte[cel.Width * cel.Height * 4];
        for (int i = 0, o = 0; i < cel.Width * cel.Height; i++, o += 4)
        {
            if (depth == 16)
            {
                rgba[o] = rgba[o + 1] = rgba[o + 2] = raw[i * 2];
                rgba[o + 3] = raw[i * 2 + 1];
            }
            else
            {
                byte index = raw[i];
                uint color = index == transparentIndex ? 0 : palette[index];
                rgba[o] = (byte)(color >> 24);
                rgba[o + 1] = (byte)(color >> 16);
                rgba[o + 2] = (byte)(color >> 8);
                rgba[o + 3] = (byte)color;
            }
        }

        return rgba;
    }

    private void Composite(byte[] into, byte[] cel, in Cel at, int opacity)
    {
        for (int row = 0; row < at.Height; row++)
        {
            int y = at.Y + row;
            if (y < 0 || y >= Height) continue;

            for (int column = 0; column < at.Width; column++)
            {
                int x = at.X + column;
                if (x < 0 || x >= Width) continue;

                int s = (row * at.Width + column) * 4;
                int sa = cel[s + 3] * opacity / 255;
                if (sa == 0) continue;

                int d = (y * Width + x) * 4;
                int da = into[d + 3];
                int outA = sa + da * (255 - sa) / 255;
                if (outA == 0) continue;

                // Straight alpha over, the way Aseprite composes a normal layer
                for (int c = 0; c < 3; c++)
                    into[d + c] = (byte)((cel[s + c] * sa + into[d + c] * da * (255 - sa) / 255) / outA);
                into[d + 3] = (byte)outA;
            }
        }
    }

    private static uint Pack(byte r, byte g, byte b, byte a) => ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at, 2));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at, 4));
}
