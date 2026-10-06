using System.Numerics;

using Horizon.OpenGL.Assets;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A picture. Either a named region of the skin or a whole texture of its own, such as a portrait.
/// Unless given a size it is as big as its art. A region that has frames is played as an animation.
/// </summary>
public class Image : UIComponent
{
    private string region = string.Empty;
    private string[] frames = [];
    private float frameTimer;
    private int frame;

    /// <summary>
    /// The name of the skin region (or any sprite of the skin's sheet) to show. Ignored while
    /// <see cref="Texture"/> is set.
    /// </summary>
    public string Region
    {
        get => region;
        set
        {
            if (region == value)
                return;

            region = value;
            frames = [];
            frame = 0;
            frameTimer = 0.0f;
        }
    }

    /// <summary>Whether a region that has frames plays through them. Off, it stays on its first.</summary>
    public bool Animated { get; set; } = true;

    /// <summary>A texture to show instead of a region of the skin.</summary>
    public Texture? Texture { get; set; }

    /// <summary>
    /// The part of <see cref="Texture"/> that is shown, in pixels from its top left corner: one sprite of a
    /// sheet, say. The whole texture while <see cref="SourceSize"/> is zero.
    /// </summary>
    public Vector2 SourcePosition { get; set; }

    /// <inheritdoc cref="SourcePosition"/>
    public Vector2 SourceSize { get; set; }

    public Vector4 Tint { get; set; } = Vector4.One;

    public Image()
    { }

    public Image(string region)
    {
        Region = region;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        if (Texture is { } texture)
            return SourceSize != Vector2.Zero ? SourceSize : new Vector2(texture.Width, texture.Height);

        return skin.TryGetRegion(Region, out var region) ? region.Size : Vector2.Zero;
    }

    protected override void Update(float dt)
    {
        if (Animated && frames.Length > 1)
            frameTimer += dt;
    }

    protected override void Paint(UIDrawList list)
    {
        if (Texture is { } texture)
        {
            if (SourceSize != Vector2.Zero)
                list.Image(texture, Bounds, SourcePosition, SourcePosition + SourceSize, Tint);
            else
                list.Image(texture, Bounds, Tint);
            return;
        }

        if (!list.Skin.TryGetRegion(Region, out var first))
            return;

        if (first.Frames > 1 && Animated)
        {
            // Every frame is a region of its own, named after the first. The names are only made once.
            if (frames.Length != first.Frames)
            {
                frames = new string[first.Frames];
                for (int i = 0; i < frames.Length; i++)
                    frames[i] = $"{Region}#{i}";
            }

            while (frameTimer >= first.FrameTime && first.FrameTime > 0.0f)
            {
                frameTimer -= first.FrameTime;
                frame = (frame + 1) % frames.Length;
            }

            // A frame that isn't in the atlas yet is asked for by this, and shows up a moment later.
            if (list.Skin.TryGetRegion(frames[frame], out var current))
                first = current;
        }

        list.NineSlice(first, Bounds, Tint);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("region", () => Region, value => Region = value);
        Expose("animated", () => Animated, value => Animated = value);
        Expose("tint", () => Tint, value => Tint = value);
    }
}
