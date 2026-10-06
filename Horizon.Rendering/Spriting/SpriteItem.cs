using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// One quad for the sprite renderer to draw: where it is, which part of which texture it shows and what it is tinted with.
/// This is what every <see cref="Sprite"/> is turned into before it is drawn, and it can be handed to a
/// <see cref="SpriteBatch"/> directly (see <see cref="SpriteBatch.Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteTexture}, Camera?)"/>)
/// to draw things that aren't sprites, like the quads of a UI.
/// 64 bytes, laid out exactly like the std430 <c>SpriteItem</c> struct in shaders/spritebatch/sprites.vert.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct SpriteItem
{
    /// <summary>The value of the texture slot for a quad that shows no texture, just its colour.</summary>
    public const uint NoTexture = 0xFF;

    // Set in Flags (above the texture slot) when only the alpha of the texture is used, the way fonts are drawn:
    // the texture says where the ink is and the colour says what colour it is
    public const uint CoverageFlag = 0x100;

    // Set in Flags when texels are to be blended where their edges meet, instead of every screen pixel taking the
    // nearest one. For pixel art drawn at a size that isn't a whole multiple of itself, which otherwise comes out
    // with some of its pixels wider than others. At a whole multiple it makes no difference.
    public const uint SmoothFlag = 0x200;

    // Set in Flags for a quad that is a quarter of a disc rather than a square, which is what the corners of a rounded box are made of.
    // TexMin and TexMax then say how far each corner of the quad is from the middle of the disc (1 is on its edge),
    // and Ring how much of it is filled. The edge is smoothed over a pixel, whatever size it is drawn at.
    public const uint CornerFlag = 0x400;

    public const uint White = 0xFFFFFFFF;

    // The bottom left corner of the quad
    public Vector2 Origin;

    // From the bottom left corner to the bottom right one, and to the top left one.
    // Anything a 2D transform can do to a quad fits in these: turning, flipping, stretching.
    public Vector2 AxisX;
    public Vector2 AxisY;

    // The texels (pixels of the texture, counted from its top left) shown at the top left and the bottom right corner
    public Vector2 TexMin;
    public Vector2 TexMax;

    // How fast the quad is moving across the world, in units a second. Nothing is moved by this: it is what a
    // renderer that blurs motion goes by (see DeferredRenderer2D), anything that doesn't say stands still
    public Vector2 Motion;

    // RGBA, 8 bits each with red in the low byte, see PackColor
    public uint Color;

    // The texture slot in the low byte (or NoTexture), the flags above it
    public uint Flags;

    // Where the quad sits along Z, for when the depth test is on
    public float Depth;

    // Only for a quad with the CornerFlag. How far in from the edge of the disc it is filled, as a share of the radius.
    // 1 is all of it, less leaves a hole in the middle and makes it the corner of an outline
    public float Ring;

    public static readonly uint SizeInBytes = (uint)Unsafe.SizeOf<SpriteItem>();

    /// <summary>
    /// A rectangle that isn't turned, which is what a UI is made of.
    /// </summary>
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

    /// <summary>
    /// Helper method to pack a colour (0 to 1 for every channel) the way <see cref="Color"/> wants it.
    /// </summary>
    public static uint PackColor(Vector4 color)
    {
        color = Vector4.Clamp(color, Vector4.Zero, Vector4.One) * 255.0f + new Vector4(0.5f);
        return (uint)color.X | (uint)color.Y << 8 | (uint)color.Z << 16 | (uint)color.W << 24;
    }
}

/// <summary>
/// A texture for <see cref="SpriteItem"/>s to show, in one of the slots of a draw call.
/// </summary>
/// <param name="Handle">The GL texture.</param>
/// <param name="Size">Its size in texels, items say what they show in texels rather than fractions.</param>
/// <param name="Sampler">A GL sampler object to filter it with, 0 to go by the settings of the texture itself.</param>
public readonly record struct SpriteTexture(uint Handle, Vector2 Size, uint Sampler = 0)
{
    public SpriteTexture(Horizon.OpenGL.Assets.Texture texture, uint sampler = 0)
        : this(texture.Handle, new Vector2(texture.Width, texture.Height), sampler) { }
}

/// <summary>
/// A stretch of items that is drawn in one call, see <see cref="SpriteBatch.Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, Camera?, ReadOnlySpan{SpriteTexture})"/>.
/// </summary>
/// <param name="First">The first item of the run.</param>
/// <param name="Count">How many items it is long.</param>
/// <param name="Texture0">A texture only this run shows, in the first slot after the shared ones.</param>
/// <param name="Texture1">Another one, in the slot after that.</param>
public readonly record struct SpriteRun(int First, int Count, SpriteTexture Texture0 = default, SpriteTexture Texture1 = default);
