using System.Buffers;

using Horizon.Content;
using Horizon.Graphics;

namespace Horizon.Rendering.Text;

/// <summary>Which picture of a letter, the font, the letter, how big, how far into its pixel and how it's made (<see cref="GlyphStyle"/>).</summary>
public readonly record struct GlyphKey(int Font, char Character, ushort Size, byte Phase, GlyphStyle Style);

/// <summary>How a letter's picture is made, part of its key.</summary>
[Flags]
public enum GlyphStyle : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    /// <summary>The x height left where it falls, on a Retina screen, see <see cref="GlyphOutlines.Rasterize"/>.</summary>
    Unhinted = 4,
    /// <summary>A subpixel picture for a screen whose blue stripe is on the left.</summary>
    Bgr = 8
}

/// <summary>Where a letter's picture is in the <see cref="GlyphAtlas"/>, in texels, and how it sits on the pen.</summary>
public readonly record struct GlyphSlot(short X, short Y, GlyphBox Box);

/// <summary>
/// The pictures of the letters as they are drawn, made at the size they are on the screen by <see cref="GlyphOutlines"/>
/// the first time each is wanted and kept in one texture every font shares, on shelves. A letter is made on whatever
/// thread is painting, into a copy kept on this side, and the rows that changed go up to the GPU on the render thread
/// before the draw that shows them (<see cref="Upload"/>), so a letter is there the first frame it's asked for.
/// <para>
/// Two of them: <see cref="Shared"/> of grey coverage, a byte a texel, and <see cref="Subpixel"/> of a coverage for each
/// of a pixel's red, green and blue stripes, four bytes a texel, for text drawn ClearType's way (see <see cref="GlyphSmoothing"/>).
/// </para>
/// <para>
/// Sizes are a quarter of a pixel apart and the pen a quarter of a pixel at a time, so a zoom or a font size setting is a
/// few hundred letters more and no more. When it's full it's emptied at the start of the next paint and everything is
/// made again, <see cref="Generation"/> says so to whatever kept what it painted.
/// </para>
/// </summary>
public sealed class GlyphAtlas
{
    /// <summary>How many texels across and down at most. Starts smaller and grows on demand up to this cap.</summary>
    public const int SIZE = 2048;

    // Starts lean so an unused or lightly loaded atlas doesn't claim 16 MB of VRAM upfront, then doubles up to SIZE
    private const int INITIAL_SIZE = 512;

    // Cap on retained per-frame upload buffer so a one-off burst of glyphs doesn't hold onto megabytes of RAM
    private const int MAX_RETAINED_PENDING_BYTES = 64 * 1024;

    // Rows per temporary zero strip when clearing a newly created or reset atlas texture
    private const int CLEAR_STRIP_ROWS = 64;

    /// <summary>How many places across a pixel the pen can be, a letter is made for each it's drawn at.</summary>
    public const int PHASES = 4;

    /// <summary>How many steps a pixel of the font's height is cut into, a letter is made for each size it's drawn at.</summary>
    public const int SIZE_STEPS = 4;

    // Nothing bigger is made into a picture, letters that big are crisp out of the field
    private const int LARGEST = 96;

    // Between two pictures, so the filter never reads one's ink into the next
    private const int PADDING = 1;

    private readonly record struct PendingGlyph(short X, short Y, short Width, short Height, int Offset);

    /// <summary>The grey one every draw list uses.</summary>
    public static GlyphAtlas Shared { get; } = new(subpixel: false);

    /// <summary>The one of subpixel pictures, used while <see cref="GlyphSmoothing"/> says to draw that way.</summary>
    public static GlyphAtlas Subpixel { get; } = new(subpixel: true);

    private readonly bool subpixel;
    private readonly int texel;
    private readonly Lock gate = new();
    // By the key packed into a number, see Pack: a record struct's own hash mixes its five fields one at a time, for
    // every letter of every line painted. Read without the lock, every letter of every paint looks one up and only a
    // letter not made yet needs it: a lock a letter was the most of what painting a busy chat cost
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, GlyphSlot> slots = new();
    private byte[] scratch = new byte[64 * 64];
    private byte[] work = new byte[64 * 64 * 3];

    // Newly rasterized glyphs queued on the painting thread and swapped to the render thread for sub-rect upload,
    // avoiding a permanent full-atlas CPU pixel mirror and staging copy
    private List<PendingGlyph> pendingGlyphs = [];
    private byte[] pendingBytes = [];
    private int pendingByteCount;
    private List<PendingGlyph> uploadGlyphs = [];
    private byte[] uploadBytes = [];

    // Old textures kept alive briefly after growing so in-flight frames finish reading them (render thread only)
    private readonly List<(Texture Texture, int Frames)> retired = [];

    // Current texture dimensions and the shelf being filled; the next picture goes to its right
    private int atlasWidth = INITIAL_SIZE, atlasHeight = INITIAL_SIZE;
    private int shelfX, shelfY, shelfHeight;
    private bool needsClear;

    // Read outside the lock by every paint and set by another, see BeginPaint
    private volatile bool full;

    // Whether making the texture failed, it isn't tried again every frame
    private bool failed;

    // Whether anything has asked it for a letter, a subpixel one nobody uses is never made
    private volatile bool wanted;

    private GlyphAtlas(bool subpixel)
    {
        this.subpixel = subpixel;
        texel = subpixel ? 4 : 1;
        wanted = !subpixel;
    }

    /// <summary>The texture, once the render thread has made it, see <see cref="Ready"/>.</summary>
    public Texture Texture { get; private set; } = Texture.Invalid;

    // Written on the render thread after the texture, read on whatever thread paints: volatile so a weaker memory model
    // (the phones' ARM) can't show it ready before the texture is there
    private volatile bool ready;
    private volatile int generation;

    /// <summary>Whether there's a texture to draw with. Until the first upload text is drawn out of its field.</summary>
    public bool Ready => ready;

    /// <summary>Another number every time it's emptied, painting kept from before is no good after.</summary>
    public int Generation => generation;

    /// <summary>
    /// Says a subpixel atlas is wanted, so the render thread makes its texture. Until it's there letters are drawn grey.
    /// </summary>
    public void Want() => wanted = true;

    // A key as one number, the font in the top half, then the letter, the size, the phase and the style
    private static ulong Pack(in GlyphKey key) =>
        (ulong)(uint)key.Font << 32 | (ulong)key.Character << 16 | (ulong)(uint)(key.Size & 0x3FF) << 6 | (ulong)(uint)(key.Phase & 0x3) << 4 | (ulong)(uint)((byte)key.Style & 0xF);

    /// <summary>The size a letter is made at for a font drawn so tall, in <see cref="SIZE_STEPS"/>, 0 when it's too big or small to be.</summary>
    public static ushort SizeOf(float pixelHeight)
    {
        if (!(pixelHeight >= 4.0f && pixelHeight <= LARGEST))
            return 0;
        return (ushort)MathF.Round(pixelHeight * SIZE_STEPS);
    }

    /// <summary>
    /// Before a paint, empties it if it filled up during the last one. Whatever thread paints.
    /// </summary>
    public void BeginPaint()
    {
        if (!full)
            return;

        lock (gate)
        {
            if (!full)
                return;
            slots.Clear();
            shelfX = shelfY = shelfHeight = 0;
            pendingGlyphs.Clear();
            pendingByteCount = 0;
            needsClear = true;
            full = false;
            generation++;
            Horizon.Logging.Log.Info($"[GlyphAtlas] The {(subpixel ? "subpixel" : "grey")} letters filled their atlas and it was emptied, generation {generation}.");
        }
    }

    /// <summary>
    /// The picture of a letter, made now if it hasn't been. False when the letter has no ink (a space) or there's no room
    /// left, which draws it out of its field until the next paint has emptied the atlas.
    /// </summary>
    /// <param name="bold">How many pixels wider the ink is for bold, see <see cref="GlyphOutlines.Rasterize"/>.</param>
    public bool TryGet(GlyphOutlines outlines, in GlyphKey key, float bold, float slant, out GlyphSlot slot)
    {
        ulong packed = Pack(key);
        if (slots.TryGetValue(packed, out slot))
            return !slot.Box.IsEmpty;

        lock (gate)
        {
            if (slots.TryGetValue(packed, out slot))
                return !slot.Box.IsEmpty;
            if (full)
                return false;

            float pixelHeight = key.Size / (float)SIZE_STEPS;
            float shift = key.Phase / (float)PHASES;
            bool hint = (key.Style & GlyphStyle.Unhinted) == 0;
            bool made = subpixel
                ? outlines.RasterizeSubpixel(key.Character, pixelHeight, shift, bold, slant, hint, (key.Style & GlyphStyle.Bgr) != 0, ref work, ref scratch, out GlyphBox box)
                : outlines.Rasterize(key.Character, pixelHeight, shift, bold, slant, hint, ref scratch, out box);
            if (!made)
            {
                // Remembered as nothing, a space is asked about on every line
                slot = default;
                slots[packed] = slot;
                return false;
            }

            if (!Place(box.Width, box.Height, out int x, out int y))
            {
                full = true;
                slot = default;
                return false;
            }

            int bytes = box.Width * box.Height * texel;
            int offset = pendingByteCount;
            int needed = offset + bytes;
            if (pendingBytes.Length < needed)
            {
                int nextCapacity = pendingBytes.Length == 0 ? Math.Max(4096, needed) : Math.Max(needed, pendingBytes.Length * 2);
                Array.Resize(ref pendingBytes, nextCapacity);
            }
            scratch.AsSpan(0, bytes).CopyTo(pendingBytes.AsSpan(offset, bytes));
            pendingByteCount = needed;
            pendingGlyphs.Add(new PendingGlyph((short)x, (short)y, (short)box.Width, (short)box.Height, offset));

            slot = new GlyphSlot((short)x, (short)y, box);
            slots[packed] = slot;
            return true;
        }
    }

    // Room on the shelf, or on a new one under it, growing the atlas up to SIZE before declaring it full
    private bool Place(int width, int height, out int x, out int y)
    {
        x = y = 0;
        if (width + PADDING > SIZE || height + PADDING > SIZE)
            return false;

        while (true)
        {
            if (shelfX + width + PADDING <= atlasWidth && shelfY + height + PADDING <= atlasHeight)
                break;

            int nextY = shelfY + shelfHeight;
            if (width + PADDING <= atlasWidth && nextY + height + PADDING <= atlasHeight)
            {
                shelfY = nextY;
                shelfX = shelfHeight = 0;
                break;
            }

            if (atlasWidth >= SIZE && atlasHeight >= SIZE)
                return false;

            atlasWidth = Math.Min(SIZE, atlasWidth * 2);
            atlasHeight = Math.Min(SIZE, atlasHeight * 2);
        }

        x = shelfX;
        y = shelfY;
        shelfX += width + PADDING;
        shelfHeight = Math.Max(shelfHeight, height + PADDING);
        return true;
    }

    /// <summary>
    /// Makes the texture the first time it's wanted and sends up the newly rasterized glyphs. Render thread, before the draws
    /// that show them.
    /// </summary>
    public unsafe void Upload()
    {
        // Free old textures once in-flight frames have finished reading them
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            var (old, frames) = retired[i];
            if (frames >= 2)
            {
                old.Dispose();
                retired.RemoveAt(i);
            }
            else
            {
                retired[i] = (old, frames + 1);
            }
        }

        if (!ready && (failed || !wanted))
            return;

        int targetWidth, targetHeight;
        bool doClear;
        lock (gate)
        {
            targetWidth = atlasWidth;
            targetHeight = atlasHeight;
            doClear = needsClear;
            needsClear = false;

            if (pendingGlyphs.Count > 0)
            {
                (pendingGlyphs, uploadGlyphs) = (uploadGlyphs, pendingGlyphs);
                (pendingBytes, uploadBytes) = (uploadBytes, pendingBytes);
                pendingGlyphs.Clear();
                pendingByteCount = 0;
            }
        }

        if (!ready)
        {
            // Nearest, never smoothed: every letter is drawn a texel to the pixel, and a filter could only blur it. The
            // subpixel stripes are coverage, not colour, so plain numbers and never taken for sRGB
            using (AssetScope.EnterGlobal())
                Texture = Texture.Create((uint)targetWidth, (uint)targetHeight, new TextureDefinition(subpixel ? PixelFormat.Rgba8 : PixelFormat.R8, Smooth: false));
            if (!Texture.IsValid)
            {
                failed = true;
                uploadGlyphs.Clear();
                return;
            }
            Texture.Name = subpixel ? "glyphs subpixel" : "glyphs";
            ClearTexture(Texture);
            ready = true;
        }
        else if (Texture.Width != (uint)targetWidth || Texture.Height != (uint)targetHeight)
        {
            Texture grown;
            using (AssetScope.EnterGlobal())
                grown = Texture.Create((uint)targetWidth, (uint)targetHeight, new TextureDefinition(subpixel ? PixelFormat.Rgba8 : PixelFormat.R8, Smooth: false));
            if (!grown.IsValid)
            {
                uploadGlyphs.Clear();
                return;
            }
            grown.Name = subpixel ? "glyphs subpixel" : "glyphs";
            Texture old = Texture;
            Texture = grown;
            ClearTexture(Texture);
            if (!doClear)
            {
                GraphicsDevice.Current.CopyTexels(old, Texture);
                GraphicsDevice.Current.Transition(old, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal,
                    Silk.NET.Vulkan.PipelineStageFlags2.VertexShaderBit | Silk.NET.Vulkan.PipelineStageFlags2.FragmentShaderBit,
                    Silk.NET.Vulkan.AccessFlags2.ShaderSampledReadBit);
            }
            retired.Add((old, 0));
        }
        else if (doClear)
        {
            ClearTexture(Texture);
        }

        if (uploadGlyphs.Count > 0)
        {
            TexelFormat format = subpixel ? TexelFormat.Rgba8 : TexelFormat.R8;
            fixed (byte* basePtr = uploadBytes)
            {
                foreach (PendingGlyph g in uploadGlyphs)
                    GraphicsDevice.Current.UploadTexels(Texture, g.X, g.Y, (uint)g.Width, (uint)g.Height, format, basePtr + g.Offset);
            }
            uploadGlyphs.Clear();
            if (uploadBytes.Length > MAX_RETAINED_PENDING_BYTES)
                uploadBytes = [];
        }

        // The glyph atlas is sampled bindlessly, so leave it in ShaderReadOnlyOptimal after any clear/copy/upload
        if (Texture.IsValid)
        {
            GraphicsDevice.Current.Transition(Texture, Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal,
                Silk.NET.Vulkan.PipelineStageFlags2.VertexShaderBit | Silk.NET.Vulkan.PipelineStageFlags2.FragmentShaderBit,
                Silk.NET.Vulkan.AccessFlags2.ShaderSampledReadBit);
        }
    }

    // Helper method to zero a newly created or reset atlas texture in small pooled strips so no full-atlas CPU mirror is kept
    private unsafe void ClearTexture(Texture texture)
    {
        int width = (int)texture.Width;
        int height = (int)texture.Height;
        int stripRows = Math.Min(CLEAR_STRIP_ROWS, height);
        int stripBytes = width * stripRows * texel;
        TexelFormat format = subpixel ? TexelFormat.Rgba8 : TexelFormat.R8;

        byte[] zeros = ArrayPool<byte>.Shared.Rent(stripBytes);
        try
        {
            Array.Clear(zeros, 0, stripBytes);
            fixed (byte* ptr = zeros)
            {
                for (int y = 0; y < height; y += stripRows)
                {
                    int rows = Math.Min(stripRows, height - y);
                    GraphicsDevice.Current.UploadTexels(texture, 0, y, (uint)width, (uint)rows, format, ptr);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }
}
