using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// An editor as tall as its code, for a stack of them that scrolls as one (the cells of a notebook and what they printed).
// It asks for the height of its rows, up to so many, and leaves the wheel to whatever it's in while it has no more of
// itself to show. Past that many rows it's that tall and scrolls the rest the way any editor does
public partial class CodeEditor
{
    /// <summary>
    /// How many rows it grows to before it scrolls, as tall as its code until then. 0, the usual, for an editor given its
    /// size by whatever it's in.
    /// </summary>
    public int FitRows { get; set; }

    // Helper method for the size an editor that fits its code asks for, as wide as it's given and as tall as its rows
    private Vector2 FittedSize(UISkin skin)
    {
        float scale = ScaleOf(skin);
        float lineHeight = FontOf(skin).LineHeight * scale;
        EnsureRows(skin, scale);
        int shown = Math.Clamp(rows.Count, 1, FitRows);
        return new Vector2(480.0f, MathF.Ceiling(shown * lineHeight)) + Padding.Total;
    }

    // Whether all of it is in sight, so there's nothing for the wheel to do here
    private bool AllInSight => FitRows > 0 && rows.Count <= FitRows;
}
