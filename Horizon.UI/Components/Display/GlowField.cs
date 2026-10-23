using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One soft bloom of light in a <see cref="GlowField"/>, where its middle is as a share of the field (0, 0 the bottom
/// left, 1, 1 the top right), how far it reaches as a share of the field's longer side, and its colour, the alpha how
/// strong it is.
/// </summary>
public readonly record struct GlowBloom(Vector2 At, float Reach, Vector4 Color);

/// <summary>
/// A field of soft light, a base colour with a few big blooms of colour fading out over it, for glass to sit over. Glass
/// over a flat colour looks like no glass at all, there's nothing behind it to blur or bend, and a window with no
/// desktop showing through needs something of its own the way a Mac's sidebar has the wallpaper. Nothing is textured,
/// every bloom is a soft disc worked out per pixel (the same as a popup's shadow), so it's a dozen quads at any size.
/// <para>
/// A <see cref="Hole"/> is left as it is, for whatever is drawn under the module to show through there (Hex's layouts in
/// the middle of the editor). The field is painted round it in four strips.
/// </para>
/// <code>
/// var glow = module.Root.Add(new GlowField { Fill = UIFill.Both });
/// glow.UseSkin(skin);
/// </code>
/// </summary>
public class GlowField : UIComponent
{
    /// <summary>What is under the blooms.</summary>
    public Vector4 Base { get; set; } = new(0.05f, 0.06f, 0.09f, 1.0f);

    /// <summary>
    /// A colour laid over the blooms, quieting them to the window's own tone, what the window's frame is (its title bar,
    /// its bars and the gaps between its panels) where nothing else is. See-through for the blooms as they are.
    /// </summary>
    public Vector4 Veil { get; set; }

    /// <summary>The blooms, painted in this order, the later over the earlier.</summary>
    public List<GlowBloom> Blooms { get; } = [];

    /// <summary>A part of the field left unpainted, in the module's units, asked every paint. Null for none.</summary>
    public Func<UIRect?>? Hole { get; set; }

    /// <summary>
    /// The base and the blooms out of a skin's colours, the background under them, the accent low down to the left the
    /// way the sun is on a horizon, and three of the colours its code is in about the rest. Each theme lights its own field.
    /// </summary>
    public void UseSkin(UISkin skin)
    {
        UIPalette palette = skin.Palette;
        Vector4 under = palette.Field with { W = 1.0f };

        // A field that's lighter than its text is a light theme, the blooms are kept fainter on paper
        float strength = palette.IsDark ? 1.0f : 0.55f;
        Base = Vector4.Lerp(under, palette.Panel with { W = 1.0f }, 0.35f);

        Blooms.Clear();
        // Faint, they're what the glass has to show and no more. Brighter, the side panels over them went grey and olive
        Blooms.Add(new GlowBloom(new Vector2(0.06f, -0.08f), 0.55f, palette.Accent with { W = 0.22f * strength }));
        Blooms.Add(new GlowBloom(new Vector2(0.98f, 1.05f), 0.6f, palette.Syntax.Type with { W = 0.2f * strength }));
        Blooms.Add(new GlowBloom(new Vector2(0.12f, 0.95f), 0.42f, palette.Syntax.Keyword with { W = 0.18f * strength }));
        Blooms.Add(new GlowBloom(new Vector2(1.0f, 0.05f), 0.45f, palette.Syntax.Directive with { W = 0.15f * strength }));
    }

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Paint(UIDrawList list)
    {
        UIRect field = Bounds;
        if (field.IsEmpty)
            return;

        // Round the hole in four strips, the one below it, the one over it and the two beside it, each the whole field cut down
        if (Hole?.Invoke() is { IsEmpty: false } hole)
        {
            hole = hole.Intersect(field);
            PaintIn(list, new UIRect(field.Min, new Vector2(field.Max.X, hole.Min.Y)), 0);
            PaintIn(list, new UIRect(new Vector2(field.Min.X, hole.Max.Y), field.Max), 1);
            PaintIn(list, new UIRect(new Vector2(field.Min.X, hole.Min.Y), new Vector2(hole.Min.X, hole.Max.Y)), 2);
            PaintIn(list, new UIRect(new Vector2(hole.Max.X, hole.Min.Y), new Vector2(field.Max.X, hole.Max.Y)), 3);
            list.EndParts();
            return;
        }

        PaintIn(list, field, 0);
        list.EndParts();
    }

    // The base and every bloom, cut down to a part of the field. Each strip is a part of its own, a hole that moves
    // empties a strip without shifting the quads of the others
    private void PaintIn(UIDrawList list, UIRect area, int part)
    {
        list.BeginPart(part);
        if (area.Width <= 0.0f || area.Height <= 0.0f)
            return;

        list.PushClip(area);
        list.Rect(area, Base);

        UIRect field = Bounds;
        float longest = MathF.Max(field.Width, field.Height);
        foreach (GlowBloom bloom in Blooms)
        {
            Vector2 middle = field.Min + bloom.At * field.Size;
            float reach = bloom.Reach * longest;

            // A shadow with no middle to speak of is a disc that fades from its centre out to its reach
            list.Shadow(UIRect.FromCenter(middle, new Vector2(2.0f)), 0.0f, reach, bloom.Color);
        }

        list.Rect(area, Veil);
        list.PopClip();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("base", () => Base, value => Base = value);
    }
}
