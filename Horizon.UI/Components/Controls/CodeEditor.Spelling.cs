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
    private readonly List<UISpellingMark> spellingSorted = [];

    /// <summary>Replace the spelling layer. Results for previous document versions are ignored.</summary>
    public void SetSpellingMarks(int version, IReadOnlyList<UISpellingMark> marks)
    {
        spellingVersion = version;
        spelling = marks;
        spellingSorted.Clear();
        if (marks.Count > 0)
        {
            if (spellingSorted.Capacity < marks.Count)
                spellingSorted.Capacity = marks.Count;
            for (int i = 0; i < marks.Count; i++)
                spellingSorted.Add(marks[i]);
            spellingSorted.Sort(static (a, b) => a.Line.CompareTo(b.Line));
        }
    }

    private int FirstSpellingAtOrAfter(int line)
    {
        int low = 0, high = spellingSorted.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (spellingSorted[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
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
        if (spellingVersion != document.Version || spellingSorted.Count == 0)
            return;

        float dot = MathF.Max(1.0f, MathF.Round(lineHeight * 0.085f));
        Vector4 shade = skin.Palette.Error;
        for (int i = FirstSpellingAtOrAfter(firstLine); i < spellingSorted.Count; i++)
        {
            UISpellingMark mark = spellingSorted[i];
            if (mark.Line > lastLine)
                break;
            if (mark.Line < 0 || mark.Line >= document.LineCount || IsHidden(mark.Line, out _))
                continue;
            list.BeginPart(HashCode.Combine(PART_SPELLING, mark.Line, mark.Column) & 0x3FFFFFFF);
            UIRect piece = Piece(skin, scale, area, left, lineHeight, mark.Line, mark.Column, mark.Column + mark.Length);
            for (float x = piece.Min.X; x + dot <= piece.Max.X; x += dot * 2.5f)
                list.Rect(new UIRect(new Vector2(x, piece.Min.Y + 1), new Vector2(x + dot, piece.Min.Y + 1 + dot)), shade);
        }
    }
}
