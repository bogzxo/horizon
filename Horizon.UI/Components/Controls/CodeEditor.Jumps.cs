using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>An edit waiting somewhere in the text, marked by an arrow in the gutter and a faint box on its span.</summary>
/// <param name="Line">The line it starts on, from 0.</param>
/// <param name="StartColumn">Where it starts on that line.</param>
/// <param name="EndColumn">Where it ends, on the same line (a span over several lines is marked on its first).</param>
/// <param name="Tooltip">What it is, e.g. "rename count → total".</param>
public readonly record struct UIJumpMarker(int Line, int StartColumn, int EndColumn, string Tooltip);

// Next-edit suggestions elsewhere than the caret (docs/next-edit-plan.md, 2.3). What is waiting is shown with the one
// prediction renderer, the program setting the nearest as the prediction (Tab goes to it, the next Tab takes it); this
// only adds the marks: an arrow in the gutter on the line of each, a faint box on its span, and a hint after the caret's
// line such as "Tab ↓ 42". Like the agent's hints, they're good for the text version they were set at.
public partial class CodeEditor
{
    private IReadOnlyList<UIJumpMarker> jumpMarkers = [];
    private int jumpMarkersVersion = -1;
    private (int Line, string Text)? jumpHint;

    /// <summary>The marks set for the text as it is, none once it has been edited since they were set.</summary>
    public IReadOnlyList<UIJumpMarker> JumpMarkers => jumpMarkersVersion == document.Version ? jumpMarkers : [];

    /// <summary>The hint written after a line, null for none or when the text has been edited since it was set.</summary>
    public (int Line, string Text)? JumpHint => jumpMarkersVersion == document.Version ? jumpHint : null;

    /// <summary>Marks the edits waiting, for the text as it is now. An empty list takes them all away.</summary>
    public void SetJumpMarkers(IReadOnlyList<UIJumpMarker> markers)
    {
        jumpMarkers = markers ?? [];
        jumpMarkersVersion = document.Version;
    }

    /// <summary>The small hint written after the end of a line ("Tab ↓ 42"), a null or empty text for none.</summary>
    public void SetJumpHint(int line, string? text)
    {
        jumpHint = string.IsNullOrEmpty(text) ? null : (line, text);
        jumpMarkersVersion = document.Version;
    }

    /// <summary>Puts the caret at the start of a mark and scrolls it into the middle of the view.</summary>
    public void ScrollToRange(int line, int column) => GoTo(line, column);

    // Whether a line has a mark, for the gutter's arrow
    private bool HasJumpMarker(int line)
    {
        if (jumpMarkersVersion != document.Version)
            return false;
        for (int i = 0; i < jumpMarkers.Count; i++)
        {
            if (jumpMarkers[i].Line == line)
                return true;
        }
        return false;
    }

    private static Vector4 JumpColour(UISkin skin) => skin.Palette.Link;

    private void PaintJumpMarkers(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, float digit, int first, int last)
    {
        if (jumpMarkersVersion != document.Version)
            return;

        MeasurePredicted(skin, scale);
        // An indexed loop, a foreach over the interface boxes an enumerator every frame
        for (int m = 0; m < jumpMarkers.Count; m++)
        {
            UIJumpMarker marker = jumpMarkers[m];
            if (marker.Line < first || marker.Line > last || marker.Line < 0 || marker.Line >= document.LineCount || IsHidden(marker.Line, out _))
                continue;
            int length = document.LineAt(marker.Line).Length;
            int from = Math.Clamp(marker.StartColumn, 0, length), to = Math.Clamp(marker.EndColumn, from, length);
            if (to == from)
                continue;

            float top = TopOf(area, marker.Line, from, lineHeight);
            float x0 = left + ColumnX(skin, scale, marker.Line, from) + PredictedShift(marker.Line, from);
            float x1 = left + ColumnX(skin, scale, marker.Line, to - 1) + PredictedShift(marker.Line, to - 1) + Width(skin, scale, document.LineAt(marker.Line).AsSpan(to - 1, 1));
            list.Rect(new UIRect(new Vector2(x0, top - lineHeight), new Vector2(x1, top)), JumpColour(skin) with { W = 0.18f });
        }

        if (jumpHint is { } hint && hint.Line >= first && hint.Line <= last && hint.Line >= 0 && hint.Line < document.LineCount && !IsHidden(hint.Line, out _))
        {
            int length = document.LineAt(hint.Line).Length;
            float x = left + EndX(skin, scale, hint.Line, length) + PredictedShift(hint.Line, length) + digit * (SwatchesOn(hint.Line) * 2 + 3)
                + ArtWidthOn(hint.Line) + LensWidthOn(hint.Line, digit);
            for (int i = 0; i < inlineHints.Count; i++)
            {
                if (inlineHints[i].Line == hint.Line)
                    x += EndHintWidth(skin, scale, inlineHints[i].Text, digit);
            }
            list.Text(hint.Text, new Vector2(x, TopOf(area, hint.Line, length, lineHeight)), scale, JumpColour(skin) with { W = 0.85f }, markup: false);
        }
    }
}
