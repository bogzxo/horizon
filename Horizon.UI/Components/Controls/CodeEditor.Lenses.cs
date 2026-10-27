using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A few things to click after the end of a line, VS Code's code lens put on the line itself. Accept Current Change |
/// Accept Incoming Change on the first marker of a merge conflict, say.
/// </summary>
/// <param name="Line">Which line, counted from 0.</param>
/// <param name="Actions">What each one says, in order, the index of the one pressed is what the program hears.</param>
public readonly record struct UICodeLens(int Line, string[] Actions);

// The lenses, painted after the end of their line in the link colour with a bar between each, the one under the pointer
// underlined. A press on one goes to the program instead of putting the caret down there
public partial class CodeEditor
{
    /// <summary>The lines with things to click after their end. Empty for none.</summary>
    public IReadOnlyList<UICodeLens> Lenses
    {
        get => lenses;
        set
        {
            lenses = value ?? [];
            lensBoxes.Clear();
        }
    }

    private IReadOnlyList<UICodeLens> lenses = [];

    /// <summary>Called with the lens and which of its actions was pressed. Null and the lenses are only words.</summary>
    public Action<UICodeLens, int>? OnLensPressed { get; set; }

    private const string LENS_GAP = " | ";

    // Where each action was painted last, by lens and action, for the press and the underline to go by
    private readonly List<(int Lens, int Action, UIRect Box)> lensBoxes = [];

    /// <summary>The lens and the action of it under a point, from where they were painted last.</summary>
    public bool LensAt(Vector2 point, out UICodeLens lens, out int action)
    {
        for (int i = 0; i < lensBoxes.Count; i++)
        {
            var (index, which, box) = lensBoxes[i];
            if (index < lenses.Count && box.Shrink(new UIEdges(-1.0f)).Contains(point))
            {
                lens = lenses[index];
                action = which;
                return true;
            }
        }

        lens = default;
        action = -1;
        return false;
    }

    /// <summary>Where an action of the lens on a line was painted last, for a test to press. Empty when it wasn't.</summary>
    public UIRect LensBounds(int line, int action)
    {
        for (int i = 0; i < lensBoxes.Count; i++)
        {
            var (index, which, box) = lensBoxes[i];
            if (index < lenses.Count && lenses[index].Line == line && which == action)
                return box;
        }

        return default;
    }

    /// <summary>Helper method for a press on a lens, over a line or after it, handed to the program, true when it took the press.</summary>
    private bool PressLens(Vector2 point)
    {
        if (PressLensAbove(point))
            return true;
        if (OnLensPressed is not { } pressed || !LensAt(point, out UICodeLens lens, out int action))
            return false;

        pressed(lens, action);
        return true;
    }

    /// <summary>How wide the lenses on a line were painted, for what's written after them.</summary>
    private float LensWidthOn(int line, float digit)
    {
        float start = float.MaxValue, end = float.MinValue;
        for (int i = 0; i < lensBoxes.Count; i++)
        {
            var (index, _, box) = lensBoxes[i];
            if (index < lenses.Count && lenses[index].Line == line)
            {
                start = MathF.Min(start, box.Min.X);
                end = MathF.Max(end, box.Max.X);
            }
        }

        return start == float.MaxValue ? 0.0f : end - start + digit * 2;
    }

    /// <summary>Helper method to paint the lenses of the lines in sight, after the swatches, the art and the hint of each.</summary>
    private void PaintLenses(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, float digit, int firstLine, int lastLine)
    {
        lensBoxes.Clear();
        if (lenses.Count == 0)
            return;

        UIFont font = FontOf(skin);
        Vector4 link = skin.Palette.Link;
        Vector4 bar = skin.TextColor with { W = skin.TextColor.W * 0.4f };
        Vector2 pointer = IsHovered && Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : new Vector2(float.MinValue);
        float gap = font.Measure(LENS_GAP, scale, markup: false).X;

        for (int i = 0; i < lenses.Count; i++)
        {
            UICodeLens lens = lenses[i];
            if (lens.Actions is not { Length: > 0 } || lens.Line < firstLine || lens.Line > lastLine || lens.Line >= document.LineCount || IsHidden(lens.Line, out _))
                continue;

            int length = document.LineAt(lens.Line).Length;
            float x = left + EndX(skin, scale, lens.Line, length) + digit * (SwatchesOn(lens.Line) * 2 + 2) + ArtWidthOn(lens.Line);
            for (int h = 0; h < inlineHints.Count; h++)
            {
                if (inlineHints[h].Line == lens.Line)
                    x += font.Measure(inlineHints[h].Text, scale, markup: false).X + digit * 2;
            }

            float top = TopOf(area, lens.Line, length, lineHeight);
            for (int a = 0; a < lens.Actions.Length; a++)
            {
                if (a > 0)
                {
                    list.Text(LENS_GAP, new Vector2(x, top), scale, bar, markup: false);
                    x += gap;
                }

                string said = lens.Actions[a];
                float width = font.Measure(said, scale, markup: false).X;
                var box = new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + width, top));
                lensBoxes.Add((i, a, box));
                list.Text(said, new Vector2(x, top), scale, link, markup: false);

                // The one under the pointer underlined, the way a link is
                if (OnLensPressed is not null && box.Contains(pointer))
                {
                    float under = top - lineHeight + MathF.Max(1.0f, MathF.Round(lineHeight * 0.12f));
                    list.Rect(new UIRect(new Vector2(x, under), new Vector2(x + width, under + MathF.Max(1.0f, MathF.Round(scale)))), link);
                }

                x += width;
            }
        }
    }
}
