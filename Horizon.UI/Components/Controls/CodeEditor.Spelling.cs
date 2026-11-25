using System.Numerics;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>A UTF-16 range marked by the host's spell checker.</summary>
public readonly record struct UISpellingMark(int Line, int Column, int Length);

public partial class CodeEditor
{
    private const int PART_SPELLING = 537681;
    private int spellingVersion = -1;
    private IReadOnlyList<UISpellingMark> spelling = [];

    /// <summary>Replace the spelling layer. Results for previous document versions are ignored.</summary>
    public void SetSpellingMarks(int version, IReadOnlyList<UISpellingMark> marks)
    {
        spellingVersion = version;
        spelling = marks;
    }

    /// <summary>The spelling marks for the text as it is now, nothing when they were made for an older version of it.</summary>
    public IReadOnlyList<UISpellingMark> SpellingMarks => spellingVersion == document.Version ? spelling : [];

    /// <summary>Expose syntax ranges only when they match the current text and grammar.</summary>
    public bool SyntaxReady => Highlight && coloured == document.Version && ReferenceEquals(colouredWith, highlighter);

    public IReadOnlyList<SyntaxSpan> CurrentSyntaxOf(int line) =>
        SyntaxReady && (uint)line < (uint)spans.Length ? spans[line] : [];

    private void PaintSpelling(UIDrawList list, UISkin skin, float scale, UIRect area, float left,
        float lineHeight, int firstLine, int lastLine)
    {
        if (spellingVersion != document.Version)
            return;

        float dot = MathF.Max(1.0f, MathF.Round(lineHeight * 0.085f));
        Vector4 shade = skin.Palette.Error;
        for (int i = 0; i < spelling.Count; i++)
        {
            UISpellingMark mark = spelling[i];
            if (mark.Line < firstLine || mark.Line > lastLine || mark.Line >= document.LineCount || IsHidden(mark.Line, out _))
                continue;
            list.BeginPart(HashCode.Combine(PART_SPELLING, mark.Line, mark.Column) & 0x3FFFFFFF);
            UIRect piece = Piece(skin, scale, area, left, lineHeight, mark.Line, mark.Column, mark.Column + mark.Length);
            for (float x = piece.Min.X; x + dot <= piece.Max.X; x += dot * 2.5f)
                list.Rect(new UIRect(new Vector2(x, piece.Min.Y + 1), new Vector2(x + dot, piece.Min.Y + 1 + dot)), shade);
        }
    }
}
