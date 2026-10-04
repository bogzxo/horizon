using System.Numerics;

using Horizon.OpenGL.Assets;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A picture: either a named region of the skin or a whole texture of its own, such as a portrait.
/// Unless given a size it is as big as its art.
/// </summary>
public class Image : UIComponent
{
    /// <summary>The name of the skin region to show. Ignored while <see cref="Texture"/> is set.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>A texture to show instead of a region of the skin.</summary>
    public Texture? Texture { get; set; }

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
            return new Vector2(texture.Width, texture.Height);

        return skin.TryGetRegion(Region, out var region) ? region.Size : Vector2.Zero;
    }

    protected override void Paint(UIDrawList list)
    {
        if (Texture is { } texture)
            list.Image(texture, Bounds, Tint);
        else if (list.Skin.TryGetRegion(Region, out var region))
            list.NineSlice(region, Bounds, Tint);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("region", () => Region, value => Region = value);
        Expose("tint", () => Tint, value => Tint = value);
    }
}
