using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Text;

namespace Horizon.UI.Drawing;

// Icons that move (UIAnimatedIcons). An icon of the font drawn turned, grown, moved or faded by where its motion's tween
// has got to, and a paint with any moving icon in it asking the window for frames a little longer, so a Hex that
// sleeps between keys doesn't leave a spinner stopped halfway round.
public sealed partial class UIDrawList
{
    // How long a moving icon keeps frames coming after the paint it was in, in seconds
    private const double MOVING_FRAMES = 0.25;

    // Whether this paint has asked for frames for a moving icon already, once a paint is plenty
    private bool movingAsked;

    /// <summary>Whether anything painted since <see cref="Begin"/> was an icon that moves.</summary>
    public bool Moving => movingAsked;

    /// <summary>Frames asked for, once a paint, while something on it moves.</summary>
    private void KeepMoving()
    {
        if (movingAsked || !Motion)
            return;

        UIAnimatedIcons.Used();
        movingAsked = true;
        Uncacheable();
        GameEngine.Instance?.WindowManager?.RequestFrames(MOVING_FRAMES);
    }

    /// <summary>
    /// An icon of the font drawn where its motion has it now. Grown about its middle, moved, faded, and turned by
    /// turning the two sides of its quad (a quad of the sprite batch is a parallelogram, an origin and two axes, which is
    /// how italics lean). One that a clip cuts is drawn as it is, unturned, a turned quad can't be trimmed square.
    /// </summary>
    private void MovingSymbol(in UISymbolGlyph symbol, UIRect rect, Vector4 color, UIIconMotion motion)
    {
        CharDefinition glyph = symbol.Definition;
        float side = MathF.Round(MathF.Min(rect.Width, rect.Height));
        var (turn, grown, moved, alpha) = UIAnimatedIcons.Now(motion, side);

        float scaled = side * grown;
        float scale = scaled / symbol.Em;
        Vector2 middle = rect.Center + moved;
        Vector2 corner = middle - new Vector2(scaled * 0.5f, -scaled * 0.5f);

        Vector2 topLeft = new(corner.X + glyph.Offset.X * scale, corner.Y - glyph.Offset.Y * scale);
        Vector2 size = glyph.Size * scale;
        var box = new UIRect(new Vector2(topLeft.X, topLeft.Y - size.Y), new Vector2(topLeft.X + size.X, topLeft.Y));

        int before = itemCount;
        bool whole = box.Intersect(clip) == box;
        Quad(box, glyph.Position, glyph.Position + glyph.TexelSize, SpriteItem.PackColor(color with { W = color.W * alpha }),
            ImageSlot(symbol.Texture) | SpriteItem.FieldFlag, symbol.FieldSlope);

        if (turn != 0.0f)
            TurnFrameQuad(turn, middle);
        if (turn != 0.0f && whole && itemCount > before)
        {
            // Turned about the middle of the icon's square rather than its picture's, so a lopsided glyph goes round in place
            ref SpriteItem item = ref items[itemCount - 1];
            Vector2 pivot = (middle * visualScale + visualOffset) * this.scale + origin;
            var (sin, cos) = MathF.SinCos(turn);
            Vector2 Turned(Vector2 v) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
            item.Origin = pivot + Turned(item.Origin - pivot);
            item.AxisX = Turned(item.AxisX);
            item.AxisY = Turned(item.AxisY);
        }

        KeepMoving();
    }
}
