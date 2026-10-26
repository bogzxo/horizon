using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// A list whose rows are a chooser's (UIChoice), a symbol, the menu quiet in front, the row, a folder or an author dim
// after it, keys as keycaps, and the letters typed lit up in the accent wherever they landed
public partial class ListBox
{
    /// <summary>
    /// The row in its parts for an item, a chooser's (<see cref="UIChooser"/>), drawn in place of <see cref="Columns"/>.
    /// Null for none, or for an item that is only its text.
    /// </summary>
    public Func<string, UIChoice?>? Choice { get; set; }

    private string query = string.Empty;

    /// <summary>What was typed to narrow the list down, the letters of it a <see cref="Choice"/> row has are lit up in the accent.</summary>
    public string Query
    {
        get => query;
        set
        {
            if (query == value)
                return;
            query = value ?? string.Empty;
            matched.Clear();
        }
    }

    /// <summary>Whether the <see cref="Choice"/> rows keep room for a symbol in front, so they line up when only some have one.</summary>
    public bool IconRoom { get; set; }

    // What each item has lit up for what's typed now, worked out once and not every time the row is painted
    private readonly Dictionary<string, List<(int Part, int Start, int Length)>> matched = new(StringComparer.Ordinal);

    // How far apart the pieces of a row in segments are, and how far its detail is from the rest
    private const float SEGMENT_GAP = 10.0f, DETAIL_INDENT = 10.0f;

    /// <summary>
    /// The letters of an item's row that match <see cref="Query"/>, each by the part they're in, where in it they start and
    /// how many. The parts are its quiet word, its text and its detail (0, 1 and 2), or each of its
    /// <see cref="UIChoice.Segments"/> by its number. The row's own text is looked in first, the rest only when the letters
    /// typed aren't all in there, a file's folder for a path typed. Empty for an item with no row in parts.
    /// </summary>
    public List<(int Part, int Start, int Length)> Matched(string item)
    {
        if (matched.TryGetValue(item, out var known))
            return known;

        var found = new List<(int Part, int Start, int Length)>();
        if (Choice?.Invoke(item) is { } choice && query.Trim().Length > 0)
        {
            if (choice.Segments is { Count: > 0 } segments)
                Spread(UIChooser.MatchSpans(string.Join(" ", segments.Select(piece => piece.Text)), query), [.. Starts(segments)], [.. segments.Select(piece => piece.Text.Length)], found);
            else if (UIChooser.MatchSpans(choice.Text, query) is { Count: > 0 } own)
                found.AddRange(own.Select(span => (1, span.Start, span.Length)));
            else
            {
                // Where each part is in what the row is known by, a path for a file or the row written out for a command
                string id = choice.Id;
                int quietAt = choice.Quiet.Length > 0 && id.StartsWith(choice.Quiet, StringComparison.Ordinal) ? 0 : -1;
                int textAt = choice.Text.Length > 0 ? id.IndexOf(choice.Text, quietAt >= 0 ? choice.Quiet.Length : 0, StringComparison.Ordinal) : -1;
                int detailAt = -1;
                if (choice.Detail.Length > 0)
                {
                    detailAt = textAt >= 0 ? id.IndexOf(choice.Detail, textAt + choice.Text.Length, StringComparison.Ordinal) : -1;
                    if (detailAt < 0)
                        detailAt = id.IndexOf(choice.Detail, StringComparison.Ordinal);
                }

                Spread(UIChooser.MatchSpans(id, query), [quietAt, textAt, detailAt], [choice.Quiet.Length, choice.Text.Length, choice.Detail.Length], found);
            }
        }

        matched[item] = found;
        return found;
    }

    // Where each segment starts in them all written with a space between
    private static IEnumerable<int> Starts(IReadOnlyList<(string Text, UIChoiceTone Tone)> segments)
    {
        int at = 0;
        foreach (var (text, _) in segments)
        {
            yield return at;
            at += text.Length + 1;
        }
    }

    // The pieces matched in the whole of it cut up by the part they fall in, a part not found (-1) gets none
    private static void Spread(List<(int Start, int Length)> spans, int[] starts, int[] lengths, List<(int Part, int Start, int Length)> into)
    {
        foreach (var (start, length) in spans)
        {
            for (int part = 0; part < starts.Length; part++)
            {
                if (starts[part] < 0)
                    continue;

                int from = Math.Max(start, starts[part]), to = Math.Min(start + length, starts[part] + lengths[part]);
                if (to > from)
                    into.Add((part, from - starts[part], to - from));
            }
        }
    }

    /// <summary>
    /// A chooser's row, its symbol, the quiet word, the text and the detail from the left, or its segments each in its tone,
    /// the keys as keycaps from the right, and the letters typed in the accent.
    /// </summary>
    private void PaintChoice(UIDrawList list, string item, UIChoice choice, UIRect area, float scale, Vector4 ink, bool chosen)
    {
        UISkin skin = list.Skin;
        Vector4 quiet = ink with { W = ink.W * 0.6f };
        Vector4 accent = skin.AccentColor with { W = ink.W };
        float left = area.Min.X;

        if (IconRoom)
        {
            float side = IconSide(skin, scale);
            if (choice.Icon.Length > 0)
            {
                var square = new UIRect(new Vector2(left, area.Center.Y - side * 0.5f), new Vector2(left + side, area.Center.Y + side * 0.5f));
                list.Symbol(choice.Icon, square, choice.IconColor is { } color ? color with { W = color.W * ink.W } : ink with { W = ink.W * 0.7f });
            }

            left += side + ICON_GAP;
        }

        float right = PaintKeys(list, choice.Keys, area, scale, ink, chosen);
        list.PushClip(new UIRect(new Vector2(left, area.Min.Y), new Vector2(MathF.Max(left, right), area.Max.Y)));
        List<(int Part, int Start, int Length)> lit = Matched(item);

        if (choice.Segments is { Count: > 0 } segments)
        {
            for (int s = 0; s < segments.Count; s++)
            {
                var (text, tone) = segments[s];
                if (text.Length == 0)
                    continue;
                left = Write(list, text, s, lit, left, area, scale, ToneColor(skin, tone, ink, quiet, accent), accent) + SEGMENT_GAP;
            }
        }
        else
        {
            if (choice.Quiet.Length > 0)
                left = Write(list, choice.Quiet, 0, lit, left, area, scale, quiet, accent) + KEY_PAD;
            left = Write(list, choice.Text, 1, lit, left, area, scale, ink, accent);
            if (choice.Detail.Length > 0)
                Write(list, choice.Detail, 2, lit, left + DETAIL_INDENT, area, scale * DETAIL_SCALE, quiet, accent);
        }

        list.PopClip();
    }

    // How big a row's detail is written against its text, a little smaller the way VS Code writes a file's folder
    private const float DETAIL_SCALE = 0.92f;

    // The colour a piece of a row in segments is written in, the skin's colours for code for the ones that aren't plain
    private static Vector4 ToneColor(UISkin skin, UIChoiceTone tone, Vector4 ink, Vector4 quiet, Vector4 accent) => tone switch
    {
        UIChoiceTone.Dim => quiet,
        UIChoiceTone.Accent => accent,
        UIChoiceTone.Hash => skin.Syntax.Number with { W = skin.Syntax.Number.W * ink.W },
        UIChoiceTone.Author => skin.Syntax.Type with { W = skin.Syntax.Type.W * ink.W },
        UIChoiceTone.Date => skin.Syntax.Comment with { W = skin.Syntax.Comment.W * ink.W },
        _ => ink
    };

    /// <summary>
    /// Helper method to write one part of a row from the left, the letters of it that match in the accent and the rest in
    /// its own colour, giving back where it ends.
    /// </summary>
    private static float Write(UIDrawList list, string text, int part, List<(int Part, int Start, int Length)> lit, float left, UIRect area, float scale, Vector4 color, Vector4 accent)
    {
        UIFont font = list.Skin.Font;
        int at = 0;
        foreach (var (inPart, start, length) in lit)
        {
            if (inPart != part || start < at || start + length > text.Length)
                continue;

            if (start > at)
                left = Piece(list, font, text[at..start], left, area, scale, color);
            left = Piece(list, font, text.Substring(start, length), left, area, scale, accent);
            at = start + length;
        }

        if (at < text.Length)
            left = Piece(list, font, text[at..], left, area, scale, color);
        return left;
    }

    private static float Piece(UIDrawList list, UIFont font, string text, float left, UIRect area, float scale, Vector4 color)
    {
        list.Text(text, new UIRect(new Vector2(left, area.Min.Y), new Vector2(MathF.Max(left, area.Max.X), area.Max.Y)), Origin.Left, scale, color, markup: false);
        return left + font.Measure(text, scale, markup: false).X;
    }

    /// <summary>
    /// Helper method to draw keys as keycaps from the right of a row, each part of a chord a cap of its own, giving back
    /// where the rest of the row has to stop.
    /// </summary>
    private static float PaintKeys(UIDrawList list, string keys, UIRect area, float scale, Vector4 ink, bool chosen)
    {
        float right = area.Max.X;
        if (keys.Length == 0)
            return right;

        UISkin skin = list.Skin;
        Vector4 quiet = ink with { W = ink.W * 0.6f };
        float keyScale = scale * 0.82f;
        float height = MathF.Min(area.Height - 6.0f, skin.Font.LineHeight * keyScale + 6.0f);
        string[] caps = keys.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries);
        for (int c = caps.Length - 1; c >= 0; c--)
        {
            float width = MathF.Max(height, skin.Font.Measure(caps[c], keyScale, markup: false).X + KEY_PAD * 2.0f);
            var cap = new UIRect(new Vector2(right - width, area.Center.Y - height * 0.5f), new Vector2(right, area.Center.Y + height * 0.5f));
            list.RoundRect(cap, MathF.Min(skin.CornerRadius, 5.0f), ink with { W = chosen ? 0.16f : 0.08f });
            list.Text(caps[c], cap, Origin.Center, keyScale, quiet, markup: false);
            right = cap.Min.X - KEY_GAP;
        }

        return right - KEY_PAD * 2.0f;
    }
}
