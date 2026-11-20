using Horizon.Content;
using Horizon.Graphics;

namespace Horizon.Rendering.Text;

/// <summary>Which picture of a letter, the font, the letter, how big, how far into its pixel and how it's styled.</summary>
public readonly record struct GlyphKey(int Font, char Character, ushort Size, byte Phase, byte Style);

/// <summary>Where a letter's picture is in the <see cref="GlyphAtlas"/>, in texels, and how it sits on the pen.</summary>
public readonly record struct GlyphSlot(short X, short Y, GlyphBox Box);

/// <summary>
/// The pictures of the letters as they are drawn, made at the size they are on the screen by <see cref="GlyphOutlines"/>
/// the first time each is wanted and kept in one R8 texture every font shares, on shelves. A letter is made on whatever
/// thread is painting, into a copy kept on this side, and the rows that changed go up to the GPU on the render thread
/// before the draw that shows them (<see cref="Upload"/>), so a letter is there the first frame it's asked for.
/// <para>
/// Sizes are a quarter of a pixel apart and the pen a quarter of a pixel at a time, so a zoom or a font size setting is a
/// few hundred letters more and no more. When it's full it's emptied at the start of the next paint and everything is
/// made again, <see cref="Generation"/> says so to whatever kept what it painted.
/// </para>
/// </summary>
public sealed class GlyphAtlas
{
    /// <summary>How many texels across and down. 4 MB, room for thousands of letters at the sizes a UI uses.</summary>
    public const int SIZE = 2048;

    /// <summary>How many places across a pixel the pen can be, a letter is made for each it's drawn at.</summary>
    public const int PHASES = 4;

    /// <summary>How many steps a pixel of the font's height is cut into, a letter is made for each size it's drawn at.</summary>
    public const int SIZE_STEPS = 4;

    // Nothing bigger is made into a picture, letters that big are crisp out of the field
    private const int LARGEST = 96;

    // Between two pictures, so the filter never reads one's ink into the next
    private const int PADDING = 1;

    /// <summary>The one every draw list uses.</summary>
    public static GlyphAtlas Shared { get; } = new();

    private readonly Lock gate = new();
    private readonly Dictionary<GlyphKey, GlyphSlot> slots = [];
    private byte[]? pixels;
    private byte[] scratch = new byte[64 * 64];

    // The shelf being filled and how tall it is, the next picture goes to its right
    private int shelfX, shelfY, shelfHeight;

    // The rows that changed since they last went up, top in and bottom out, nothing when top isn't above bottom
    private int dirtyTop = int.MaxValue, dirtyBottom;
    private bool full;

    /// <summary>The texture, once the render thread has made it, see <see cref="Ready"/>.</summary>
    public Texture Texture { get; private set; } = Texture.Invalid;

    /// <summary>Whether there's a texture to draw with. Until the first upload text is drawn out of its field.</summary>
    public bool Ready { get; private set; }

    /// <summary>Another number every time it's emptied, painting kept from before is no good after.</summary>
    public int Generation { get; private set; }

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
            if (pixels is not null)
                Array.Clear(pixels);
            dirtyTop = 0;
            dirtyBottom = SIZE;
            full = false;
            Generation++;
        }
    }

    /// <summary>
    /// The picture of a letter, made now if it hasn't been. False when the letter has no ink (a space) or there's no room
    /// left, which draws it out of its field until the next paint has emptied the atlas.
    /// </summary>
    /// <param name="pixelHeight">How tall the font is, ascent to descent, in pixels of the screen, see <see cref="SizeOf"/>.</param>
    /// <param name="bold">How many pixels wider the ink is for bold, see <see cref="GlyphOutlines.Rasterize"/>.</param>
    public bool TryGet(GlyphOutlines outlines, in GlyphKey key, float bold, float slant, out GlyphSlot slot)
    {
        lock (gate)
        {
            if (slots.TryGetValue(key, out slot))
                return !slot.Box.IsEmpty;
            if (full)
                return false;

            float pixelHeight = key.Size / (float)SIZE_STEPS;
            float shift = key.Phase / (float)PHASES;
            if (!outlines.Rasterize(key.Character, pixelHeight, shift, bold, slant, ref scratch, out GlyphBox box))
            {
                // Remembered as nothing, a space is asked about on every line
                slot = default;
                slots[key] = slot;
                return false;
            }

            if (!Place(box.Width, box.Height, out int x, out int y))
            {
                full = true;
                slot = default;
                return false;
            }

            pixels ??= new byte[SIZE * SIZE];
            for (int row = 0; row < box.Height; row++)
                scratch.AsSpan(row * box.Width, box.Width).CopyTo(pixels.AsSpan((y + row) * SIZE + x, box.Width));

            dirtyTop = Math.Min(dirtyTop, y);
            dirtyBottom = Math.Max(dirtyBottom, y + box.Height);

            slot = new GlyphSlot((short)x, (short)y, box);
            slots[key] = slot;
            return true;
        }
    }

    // Room on the shelf, or on a new one under it
    private bool Place(int width, int height, out int x, out int y)
    {
        x = y = 0;
        if (width + PADDING > SIZE || height + PADDING > SIZE)
            return false;

        if (shelfX + width + PADDING > SIZE)
        {
            shelfY += shelfHeight;
            shelfX = shelfHeight = 0;
        }

        if (shelfY + height + PADDING > SIZE)
            return false;

        x = shelfX;
        y = shelfY;
        shelfX += width + PADDING;
        shelfHeight = Math.Max(shelfHeight, height + PADDING);
        return true;
    }

    /// <summary>
    /// Makes the texture the first time and sends up the rows that changed. Render thread, before the draws that show them.
    /// </summary>
    public unsafe void Upload()
    {
        if (!Ready)
        {
            using (AssetScope.EnterGlobal())
                Texture = Texture.Create(SIZE, SIZE, new TextureDefinition(PixelFormat.R8, Smooth: true));
            if (!Texture.IsValid)
                return;
            Texture.Name = "glyphs";

            // What it held before it was made isn't anything, it all goes up the once
            lock (gate)
            {
                pixels ??= new byte[SIZE * SIZE];
                dirtyTop = 0;
                dirtyBottom = SIZE;
            }
            Ready = true;
        }

        lock (gate)
        {
            if (dirtyTop >= dirtyBottom || pixels is null)
                return;

            fixed (byte* texels = &pixels[dirtyTop * SIZE])
                GraphicsDevice.Current.UploadTexels(Texture, 0, dirtyTop, SIZE, (uint)(dirtyBottom - dirtyTop), TexelFormat.R8, texels);
            dirtyTop = int.MaxValue;
            dirtyBottom = 0;
        }
    }
}
