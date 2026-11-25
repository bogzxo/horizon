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
    private const float MIN_VISIBLE_ALPHA = 0.001f;
    private const float OPAQUE_ALPHA = 0.999f;

    // Cached hole from PaintVersion so PaintVersion and Paint evaluate Hole once per paint and always agree
    private UIRect? resolvedHole;
    private bool hasResolvedHole;

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

    protected override long PaintVersion
    {
        get
        {
            UIRect bounds = Bounds;
            UIRect? hole = ResolveHole(bounds);
            resolvedHole = hole;
            hasResolvedHole = true;

            var hash = new HashCode();
            hash.Add(bounds);
            hash.Add(Veil);
            if (hole is { } cut)
            {
                hash.Add(true);
                hash.Add(cut);
            }
            else
            {
                hash.Add(false);
            }

            // When the veil is opaque, the base and bloom layers underneath are occluded and skipped
            if (Veil.W < OPAQUE_ALPHA)
            {
                hash.Add(Base);
                hash.Add(Blooms.Count);
                for (int i = 0; i < Blooms.Count; i++)
                {
                    GlowBloom bloom = Blooms[i];
                    hash.Add(bloom.At);
                    hash.Add(bloom.Reach);
                    hash.Add(bloom.Color);
                }
            }

            return (uint)hash.ToHashCode();
        }
    }

    private UIRect? ResolveHole(UIRect field)
    {
        if (field.IsEmpty || Hole?.Invoke() is not { IsEmpty: false } raw)
            return null;

        UIRect clipped = raw.Intersect(field);
        return clipped.IsEmpty ? null : clipped;
    }

    protected override void Paint(UIDrawList list)
    {
        UIRect field = Bounds;
        UIRect? hole = Children.Count == 0 && hasResolvedHole ? resolvedHole : ResolveHole(field);
        hasResolvedHole = false;
        if (field.IsEmpty)
            return;

        bool opaqueVeil = Veil.W >= OPAQUE_ALPHA;
        bool hasVeil = Veil.W > MIN_VISIBLE_ALPHA;
        bool hasBase = !opaqueVeil && Base.W > MIN_VISIBLE_ALPHA;
        bool hasBlooms = false;
        if (!opaqueVeil)
        {
            for (int i = 0; i < Blooms.Count; i++)
            {
                GlowBloom bloom = Blooms[i];
                if (bloom.Color.W > MIN_VISIBLE_ALPHA && bloom.Reach > 0.0f)
                {
                    hasBlooms = true;
                    break;
                }
            }
        }

        if (!hasVeil && !hasBase && !hasBlooms)
            return;

        // Round the hole in four strips, the one below it, the one over it and the two beside it, each the whole field cut down
        if (hole is { } cut)
        {
            PaintIn(list, new UIRect(field.Min, new Vector2(field.Max.X, cut.Min.Y)), 0, field, opaqueVeil, hasBase, hasBlooms, hasVeil);
            PaintIn(list, new UIRect(new Vector2(field.Min.X, cut.Max.Y), field.Max), 1, field, opaqueVeil, hasBase, hasBlooms, hasVeil);
            PaintIn(list, new UIRect(new Vector2(field.Min.X, cut.Min.Y), new Vector2(cut.Min.X, cut.Max.Y)), 2, field, opaqueVeil, hasBase, hasBlooms, hasVeil);
            PaintIn(list, new UIRect(new Vector2(cut.Max.X, cut.Min.Y), new Vector2(field.Max.X, cut.Max.Y)), 3, field, opaqueVeil, hasBase, hasBlooms, hasVeil);
            list.EndParts();
            return;
        }

        PaintIn(list, field, 0, field, opaqueVeil, hasBase, hasBlooms, hasVeil);
        list.EndParts();
    }

    // The base and every bloom, cut down to a part of the field. Each strip is a part of its own, a hole that moves
    // empties a strip without shifting the quads of the others
    private void PaintIn(UIDrawList list, UIRect area, int part, UIRect field, bool opaqueVeil, bool hasBase, bool hasBlooms, bool hasVeil)
    {
        if (area.Width <= 0.0f || area.Height <= 0.0f)
            return;

        list.BeginPart(part);

        if (opaqueVeil)
        {
            // An opaque veil completely occludes the base and bloom layers underneath it
            list.Rect(area, Veil);
            return;
        }

        if (hasBlooms)
            list.PushClip(area);

        if (hasBase)
            list.Rect(area, Base);

        if (hasBlooms)
        {
            float longest = MathF.Max(field.Width, field.Height);
            for (int i = 0; i < Blooms.Count; i++)
            {
                GlowBloom bloom = Blooms[i];
                if (bloom.Color.W <= MIN_VISIBLE_ALPHA || bloom.Reach <= 0.0f)
                    continue;

                Vector2 middle = field.Min + bloom.At * field.Size;
                float reach = bloom.Reach * longest;

                // A shadow with no middle to speak of is a disc that fades from its centre out to its reach
                list.Shadow(UIRect.FromCenter(middle, new Vector2(2.0f)), 0.0f, reach, bloom.Color);
            }
        }

        if (hasVeil)
            list.Rect(area, Veil);

        if (hasBlooms)
            list.PopClip();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("base", () => Base, value => Base = value);
    }
}
