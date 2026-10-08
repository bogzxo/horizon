using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
using Horizon.Graphics;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// One quad for the sprite renderer to draw, where it is, which part of which texture it shows and what it is tinted with.
/// This is what every <see cref="Sprite"/> is turned into before it is drawn, and it can be handed to a
/// <see cref="SpriteBatch"/> directly (see <see cref="SpriteBatch.Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteTexture}, Camera?)"/>)
/// to draw things that aren't sprites, like the quads of a UI.
/// 64 bytes, laid out exactly like the <c>SpriteItem</c> struct in shaders/spritebatch/sprites.slang.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct SpriteItem
{
    /// <summary>
    /// The low sixteen bits of <see cref="Flags"/> say which texture the quad shows. Written by whoever makes the item
    /// as a slot (0, 1, 2...) into the textures handed to the draw, which the renderer swaps for the texture's place
    /// in the bindless table on its way to the GPU.
    /// </summary>
    public const uint TextureMask = 0xFFFF;

    /// <summary>The value of the texture bits for a quad that shows no texture, just its colour.</summary>
    public const uint NoTexture = 0xFFFF;

    // Set in Flags (above the texture) when only the alpha of the texture is used, the way fonts are drawn.
    // The texture says where the ink is and the colour says what colour it is
    public const uint CoverageFlag = 0x10000;

    // Set in Flags when texels are to be blended where their edges meet, instead of every screen pixel taking the
    // nearest one. For pixel art drawn at a size that isn't a whole multiple of itself, which otherwise comes out
    // with some of its pixels wider than others. At a whole multiple it makes no difference
    public const uint SmoothFlag = 0x20000;

    // Set in Flags for a quad that is a quarter of a disc rather than a square, which is what the corners of a rounded box are made of.
    // TexMin and TexMax then say how far each corner of the quad is from the middle of the disc (1 is on its edge),
    // and Ring how much of it is filled. The edge is smoothed over a pixel, whatever size it is drawn at
    public const uint CornerFlag = 0x40000;

    // Set in Flags for a quad that is being flashed. Its colour is then not multiplied in but painted over the texture,
    // as much of it as Ring says (1 is nothing but the colour in the shape of the sprite), and it glows whatever the light is.
    // A tint can't do this, multiplying only ever makes a sprite darker
    public const uint FlashFlag = 0x80000;

    // Set in Flags for a quad that blocks light, see Sprite.CastsShadows. Where it is drawn goes into the shadow mask
    // of the renderer, which makes a distance field of it for the lights to march
    public const uint ShadowFlag = 0x100000;

    // Set in Flags for a quad whose texture is a signed distance field (text, see DistanceFieldFont), 128 on the
    // edge of the ink, up inside, down outside. The shader turns it into a crisp edge at any size, the colour is the item's
    public const uint FieldFlag = 0x200000;

    // Set in Flags for a flashed quad that is a lamp, its flash lights what is round it in the path traced lighting
    // rather than only showing bright, see Sprite.FlashLights
    public const uint LampFlag = 0x400000;

    public const uint White = 0xFFFFFFFF;

    // The bottom left corner of the quad
    public Vector2 Origin;

    // From the bottom left corner to the bottom right one, and to the top left one.
    // Anything a 2D transform can do to a quad fits in these, turning, flipping, stretching
    public Vector2 AxisX;
    public Vector2 AxisY;

    // The texels (pixels of the texture, counted from its top left) shown at the top left and the bottom right corner
    public Vector2 TexMin;
    public Vector2 TexMax;

    // How fast the quad is moving across the world, in units a second. Nothing is moved by this, it is what a
    // renderer that blurs motion goes by (see DeferredRenderer2D), anything that doesn't say stands still
    public Vector2 Motion;

    // RGBA, 8 bits each with red in the low byte, see PackColor
    public uint Color;

    // The texture in the low sixteen bits (or NoTexture), the flags above it
    public uint Flags;

    // Where the quad sits along Z, for when the depth test is on
    public float Depth;

    // For a quad with the CornerFlag, how far in from the edge of the disc it is filled as a share of the radius.
    // 1 is all of it, less leaves a hole in the middle and makes it the corner of an outline.
    // For one with the FlashFlag, how much of the flash there is from 0 to 1
    public float Ring;

    public static readonly uint SizeInBytes = (uint)Unsafe.SizeOf<SpriteItem>();

    /// <summary>The slot (or table index) in some flags.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint TextureOf(uint flags) => flags & TextureMask;

    /// <summary>The same flags with another texture in them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint WithTexture(uint flags, uint texture) => (flags & ~TextureMask) | (texture & TextureMask);

    /// <summary>A rectangle that isn't turned, which is what a UI is made of.</summary>
    /// <param name="min">The bottom left corner.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteItem Rectangle(Vector2 min, Vector2 max, Vector2 texMin, Vector2 texMax, uint color, uint flags) => new()
    {
        Origin = min,
        AxisX = new Vector2(max.X - min.X, 0.0f),
        AxisY = new Vector2(0.0f, max.Y - min.Y),
        TexMin = texMin,
        TexMax = texMax,
        Color = color,
        Flags = flags
    };

    /// <summary>
    /// The quad a model matrix makes of a square of 1 by 1 around the origin, which is what every transform
    /// in the engine is built for.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpriteItem FromModel(in Matrix4x4 model, Vector2 texMin, Vector2 texMax, uint color, uint flags)
    {
        Vector2 axisX = new(model.M11, model.M12);
        Vector2 axisY = new(model.M21, model.M22);

        return new SpriteItem
        {
            Origin = new Vector2(model.M41, model.M42) - (axisX + axisY) * 0.5f,
            AxisX = axisX,
            AxisY = axisY,
            TexMin = texMin,
            TexMax = texMax,
            Color = color,
            Flags = flags,
            Depth = model.M43
        };
    }

    // Further than this (in units of the world) between two ticks is being put somewhere else, not moving there
    private const float TELEPORT = 256.0f;

    /// <summary>
    /// What a quad looks like partway (0 to 1) from one snapshot of it to the next. Where it is, how it is turned and
    /// stretched, its colour and how fast it is going are mixed, which part of which texture it shows and how it is
    /// drawn are what they were until the moment is all the way at the newer one. See <see cref="CanBlend"/> for when
    /// two snapshots are not to be mixed at all.
    /// </summary>
    public static SpriteItem Blend(in SpriteItem from, in SpriteItem to, float amount)
    {
        if (amount >= 1.0f)
            return to;

        SpriteItem item = from;
        item.Origin = Interpolate.Linear(from.Origin, to.Origin, amount);
        item.AxisX = Interpolate.Linear(from.AxisX, to.AxisX, amount);
        item.AxisY = Interpolate.Linear(from.AxisY, to.AxisY, amount);
        item.Motion = Interpolate.Linear(from.Motion, to.Motion, amount);
        item.Color = Interpolate.PackedColor(from.Color, to.Color, amount);
        item.Ring = Interpolate.Linear(from.Ring, to.Ring, amount);
        return item;
    }

    /// <summary>
    /// Whether a quad went from one snapshot to the next in a way that can be shown on its way, not flipped over (which
    /// mixed would squash it flat halfway) and not put somewhere else entirely.
    /// </summary>
    public static bool CanBlend(in SpriteItem from, in SpriteItem to)
    {
        // Turning keeps the way round the corners go, mirroring reverses it
        float before = from.AxisX.X * from.AxisY.Y - from.AxisX.Y * from.AxisY.X;
        float after = to.AxisX.X * to.AxisY.Y - to.AxisX.Y * to.AxisY.X;
        if (before * after < 0.0f)
            return false;

        Vector2 moved = (to.Origin + (to.AxisX + to.AxisY) * 0.5f) - (from.Origin + (from.AxisX + from.AxisY) * 0.5f);
        return moved.LengthSquared() <= TELEPORT * TELEPORT;
    }

    /// <summary>Helper method to pack a colour (0 to 1 for every channel) the way <see cref="Color"/> wants it.</summary>
    public static uint PackColor(Vector4 color)
    {
        color = Vector4.Clamp(color, Vector4.Zero, Vector4.One) * 255.0f + new Vector4(0.5f);
        return (uint)color.X | (uint)color.Y << 8 | (uint)color.Z << 16 | (uint)color.W << 24;
    }
}

/// <summary>
/// A texture for <see cref="SpriteItem"/>s to show, in one of the slots of a draw.
/// </summary>
/// <param name="Texture">The texture, null for an empty slot.</param>
/// <param name="Sampler">A sampler to read it through, 0 to go by the settings of the texture itself.</param>
public readonly record struct SpriteTexture(Texture? Texture, uint Sampler = 0)
{
    public SpriteTexture(SpriteSheet sheet, uint sampler = 0) : this(sheet.Texture, sampler) { }

    /// <summary>Whether there is a texture in the slot.</summary>
    public bool IsValid => Texture is { IsValid: true };

    /// <summary>The texture's size in texels, items say what they show in texels rather than fractions.</summary>
    public Vector2 Size => Texture?.Size ?? Vector2.Zero;

    /// <summary>Where the texture (read through the sampler) is in the bindless table, which is what goes into the item. Render thread.</summary>
    public uint Index => Texture is { IsValid: true } texture ? texture.Bindless(Sampler) : SpriteItem.NoTexture;
}

/// <summary>
/// A stretch of items that is drawn with textures of its own, see <see cref="SpriteBatch.Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, Camera?, ReadOnlySpan{SpriteTexture})"/>.
/// </summary>
/// <param name="First">The first item of the run.</param>
/// <param name="Count">How many items it is long.</param>
/// <param name="Texture0">A texture only this run shows, in the first slot after the shared ones.</param>
/// <param name="Texture1">Another one, in the slot after that.</param>
public readonly record struct SpriteRun(int First, int Count, SpriteTexture Texture0 = default, SpriteTexture Texture1 = default);
