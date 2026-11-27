using System.Numerics;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

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

    /// <summary>
    /// An image file to draw the whole of, a PNG, instead of a region of the skin. It goes into the skin's atlas and
    /// scales with the rest of the UI. Set from a layout with <c>file: "Assets/ui/logo.png"</c>.
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>A texture to show instead of a region of the skin.</summary>
    public Texture? Texture { get; set; }

    /// <summary>
    /// The part of <see cref="Texture"/> that is shown, in pixels from its top left corner, one sprite of a
    /// sheet, say. The whole texture while <see cref="SourceSize"/> is zero.
    /// </summary>
    public Vector2 SourcePosition { get; set; }

    /// <inheritdoc cref="SourcePosition"/>
    public Vector2 SourceSize { get; set; }

    public Vector4 Tint { get; set; } = Vector4.One;

    /// <summary>
    /// Somebody else's atlas to draw out of (a character's, the one the fight draws them from), with
    /// <see cref="AtlasKey"/> saying which region. Takes the place of the region, the file and the texture.
    /// </summary>
    public TextureAtlas? Atlas { get; set; }

    /// <summary>The region of <see cref="Atlas"/> to show, the key it was asked for under.</summary>
    public string AtlasKey { get; set; } = string.Empty;

    /// <summary>Whether what's shown is flipped left to right.</summary>
    public bool Mirrored { get; set; }

    public Image()
    { }

    // The shape of the file, worked out the first time it's asked for, and which file that was
    private UIWrapShape? fileShape;
    private string? fileShapeOf;

    /// <summary>
    /// What text wrapping round the image goes round, the shape of what is drawn in its <see cref="File"/> (the
    /// see-through round it isn't) unless it is given one. The box of it for anything else.
    /// </summary>
    public override UIWrapShape? WrapShape
    {
        get
        {
            if (base.WrapShape is { } given || File.Length == 0) return base.WrapShape;
            if (fileShapeOf != File)
            {
                fileShapeOf = File;
                fileShape = UIWrapShape.FromFile(File);
            }

            return fileShape;
        }
        set => base.WrapShape = value;
    }

    public Image(string region)
    {
        Region = region;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        if (Atlas is { } atlas)
            return SourceSize != Vector2.Zero ? SourceSize : atlas.TryGet(AtlasKey, out var found) ? found.FrameSize : Vector2.Zero;

        if (Texture is { } texture)
            return SourceSize != Vector2.Zero ? SourceSize : new Vector2(texture.Width, texture.Height);

        if (File.Length > 0)
            return skin.ImageSize(File);

        return skin.TryGetRegion(Region, out var region) ? region.Size : Vector2.Zero;
    }

    protected override void Update(float dt)
    {
        if (Animated && frames.Length > 1)
            frameTimer += dt;
    }

    protected override void Paint(UIDrawList list)
    {
        if (Atlas is { } atlas)
        {
            if (AtlasKey.Length > 0) list.Image(atlas, AtlasKey, Bounds, Tint, Mirrored);
            return;
        }

        if (Texture is { } texture)
        {
            if (SourceSize != Vector2.Zero)
                list.Image(texture, Bounds, SourcePosition, SourcePosition + SourceSize, Tint);
            else
                list.Image(texture, Bounds, Tint);
            return;
        }

        if (File.Length > 0)
        {
            // Asked for here the first time, there by the next frame. Brought down to twice the size it's shown at (a
            // screen at 200%), a picture bigger than the atlas has room for, Hex's 1254 pixel icon on the About box, was
            // never there at all
            int most = Size.X > 0.0f && Size.Y > 0.0f ? Math.Clamp((int)MathF.Ceiling(MathF.Max(Size.X, Size.Y) * 2.0f), 16, 1024) : 0;
            if (list.Skin.TryGetImage(File, most, out var whole))
                list.NineSlice(whole, Bounds, Tint);
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

    protected override bool TrySetScriptProperty(string name, Horizon.HIDL.Runtime.IRuntimeValue value)
    {
        switch (name)
        {
            case "region": Region = Horizon.UI.Scripting.UIScript.ToText(value, name); return true;
            case "file": File = Horizon.UI.Scripting.UIScript.ToText(value, name); return true;
            case "animated": Animated = Horizon.UI.Scripting.UIScript.ToBoolean(value, name); return true;
            case "tint": Tint = Horizon.UI.Scripting.UIScript.ToColor(value, name); return true;
            default: return base.TrySetScriptProperty(name, value);
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("region", () => Region, value => Region = value);
        Expose("file", () => File, value => File = value);
        Expose("animated", () => Animated, value => Animated = value);
        Expose("tint", () => Tint, value => Tint = value);
    }
}
