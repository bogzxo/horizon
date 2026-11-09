using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The scroll bar down the right of the code, drawn the way every other bar is (UIScrollBar), with what was found and
// what's wrong ticked across its track where they are in the whole file, VS Code's overview ruler. The find's ticks were
// down the right edge of the minimap, against the thumb, and only with the minimap on.
public partial class CodeEditor
{
    // How tall a tick is at the least and the most, in units
    private const float TICK_LEAST = 2.0f, TICK_MOST = 4.0f;

    /// <summary>
    /// Where the code itself is drawn as it was last painted, between the gutter and the minimap, so whatever is laid over
    /// the editor (a find bar) can keep clear of the minimap and the bar. The editor's bounds before it's been painted.
    /// </summary>
    public UIRect CodeBounds => paintedSkin is { } skin ? TextArea(skin, paintedScale) : Bounds;

    /// <summary>The track of the scroll bar as it was last painted, nothing before it has been.</summary>
    public UIRect ScrollBarTrack => paintedSkin is { } skin ? BarGeometry(skin, paintedScale).Track : default;

    /// <summary>The thumb of the scroll bar as it was last painted, nothing while the code all fits.</summary>
    public UIRect ScrollBarThumb
    {
        get
        {
            if (paintedSkin is not { } skin || rows.Count * FontOf(skin).LineHeight * paintedScale <= TextArea(skin, paintedScale).Height)
                return default;

            var (track, length, _, top) = BarGeometry(skin, paintedScale);
            return new UIRect(new Vector2(track.Max.X - UIScrollBar.Width(BAR_WIDTH, draggingBar ? 1.0f : 0.0f), top - length), new Vector2(track.Max.X, top));
        }
    }

    /// <summary>Helper method to paint the bar, its ticks under the thumb, lit with the pointer on it and while it's dragged.</summary>
    private void PaintBar(UIDrawList list, UISkin skin, float lineHeight, UIRect area)
    {
        var (track, length, _, topOfThumb) = BarGeometry(skin, paintedScale);
        PaintTicks(list, skin, track);

        if (rows.Count * lineHeight <= area.Height)
            return;

        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : new Vector2(float.NaN);
        bool over = IsHovered && pointer.X >= track.Min.X && pointer.X <= Bounds.Max.X && pointer.Y >= track.Min.Y && pointer.Y <= track.Max.Y;
        float lit = draggingBar || over ? 1.0f : 0.0f;
        UIScrollBar.Paint(list, track.Max.X, track.Min.Y, track.Max.Y, topOfThumb, length, UIScrollBar.Width(BAR_WIDTH, lit), lit, draggingBar, skin.TextColor);
    }

    /// <summary>
    /// Helper method for the ticks across the track, what was found and then what's wrong over it. Ticks that would land on
    /// the same pixel as the one before are left out, a word found on every line of a long file is a column of them and not
    /// thousands of quads.
    /// </summary>
    private void PaintTicks(UIDrawList list, UISkin skin, UIRect track)
    {
        if (rows.Count == 0 || (findQuery.Length == 0 && diagnostics.Count == 0))
            return;

        float height = Math.Clamp(track.Height / rows.Count, TICK_LEAST, TICK_MOST);
        float last = float.NaN;
        if (findQuery.Length > 0)
        {
            Vector4 lit = skin.HighlightColor with { W = 0.6f };
            for (int i = 0; i < found.Count; i++)
                last = Tick(list, track, found[i].Line, height, last, lit);
        }

        last = float.NaN;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            UICodeDiagnostic diagnostic = diagnostics[i];
            if (!diagnostic.Hint)
                last = Tick(list, track, diagnostic.Line, height, last, (diagnostic.Warning ? skin.WarningColor : skin.ErrorColor) with { W = 0.75f });
        }
    }

    // One tick for a line, nothing when it would land on the last one's pixel. Where it landed
    private float Tick(UIDrawList list, UIRect track, int line, float height, float last, Vector4 colour)
    {
        if (line < 0 || line >= document.LineCount)
            return last;

        float share = (RowOf(line, 0) + 0.5f) / rows.Count;
        float y = MathF.Round(track.Max.Y - track.Height * share);
        if (y == last)
            return last;

        UIScrollBar.Tick(list, track.Min.X, track.Max.X, track.Min.Y, track.Max.Y, share, height, colour);
        return y;
    }
}
