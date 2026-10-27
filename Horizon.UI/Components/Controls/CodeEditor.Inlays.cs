using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A few words written into the middle of a line that aren't in its text, VS Code's inlay hints. The name of the parameter
/// an argument goes to, the type a name was given. Drawn faint in a pill, the code after it pushed along to make room,
/// and the caret goes past it in one step.
/// </summary>
/// <param name="Line">Which line, counted from 0.</param>
/// <param name="Column">Where on it, the hint goes just before the character there (the end of the line for after it).</param>
/// <param name="Text">What it says.</param>
/// <param name="PadLeft">Whether there's a little room before it.</param>
/// <param name="PadRight">Whether there's a little room after it.</param>
public readonly record struct UICodeInlay(int Line, int Column, string Text, bool PadLeft = false, bool PadRight = false);

// Inlay hints, words in among the code that aren't in its text. Every place that works out where a column is drawn goes
// through here for how much room the hints before it take (ColumnX, PositionAt, the wrap and the paint), so a click, the
// caret, a selection and the squiggles all line up with the text pushed along. A line is typed into before the program
// has worked its hints out again, so each line keeps the text it had when they were given, and one whose text isn't that
// any more goes without until they are, rather than with a hint stuck in the middle of a word. That catches every way a
// line changes (another caret, a replace, an undo somewhere else), not only the caret's. Lines put in or taken out above
// move them along.
public partial class CodeEditor
{
    // The hints by their line, each line's in order along it, and the text each line had when they were given
    private readonly Dictionary<int, List<UICodeInlay>> inlays = [];
    private readonly Dictionary<int, string> inlaysText = [];
    private IReadOnlyList<UICodeInlay> inlayList = [];

    // Goes up with every change to them, the rows are cut again
    private int inlaysVersion;

    // The room either side of the words inside the pill, and either side of the pill when it's padded, in digits
    private const float INLAY_INSIDE = 0.3f, INLAY_PAD = 0.5f;

    // The room before a hint after the end of a line, in digits, the room a padded hint has before it twice over
    private const float INLAY_END_GAP = 1.0f;

    /// <summary>The hints written in among the code (see <see cref="UICodeInlay"/>), for the text as it is now. Empty for none.</summary>
    public IReadOnlyList<UICodeInlay> InlayHints
    {
        get => inlayList;
        set => SetInlays(value ?? [], null);
    }

    // The hints put in, each line's text as it is now unless they've been moved along with their old texts
    private void SetInlays(IReadOnlyList<UICodeInlay> given, Dictionary<int, string>? texts)
    {
        inlayList = given;
        inlays.Clear();
        inlaysText.Clear();
        foreach (UICodeInlay inlay in inlayList)
        {
            if (!inlays.TryGetValue(inlay.Line, out List<UICodeInlay>? on))
            {
                inlays[inlay.Line] = on = [];
                if (texts is not null && texts.TryGetValue(inlay.Line, out string? kept))
                    inlaysText[inlay.Line] = kept;
                else if (texts is null && inlay.Line < document.LineCount)
                    inlaysText[inlay.Line] = document.LineAt(inlay.Line);
            }
            on.Add(inlay);
        }

        foreach (List<UICodeInlay> on in inlays.Values)
            on.Sort((a, b) => a.Column.CompareTo(b.Column));
        inlaysVersion++;
    }

    /// <summary>The hints of a line, none while its text isn't what it was when they were given.</summary>
    private List<UICodeInlay>? InlaysOn(int line)
    {
        if (inlays.Count == 0 || !inlays.TryGetValue(line, out List<UICodeInlay>? on) || line >= document.LineCount)
            return null;

        return inlaysText.TryGetValue(line, out string? was) && string.Equals(was, document.LineAt(line), StringComparison.Ordinal) ? on : null;
    }

    // Lines put in or taken out, the hints below move along with them (the way the diagnostics do), each with the text its
    // line had, so a line that was split or joined goes without
    private void MoveInlays(UITextPosition at, int by)
    {
        if (inlays.Count == 0 || by == 0)
            return;

        var moved = new List<UICodeInlay>(inlayList.Count);
        var texts = new Dictionary<int, string>();
        foreach (UICodeInlay inlay in inlayList)
        {
            int line = inlay.Line;
            if (by > 0 && (line > at.Line || (line == at.Line && inlay.Column >= at.Column)))
                line += by;
            else if (by < 0 && line > at.Line)
                line += by;
            else if (by < 0 && line > at.Line + by)
                continue;

            moved.Add(inlay with { Line = line });
            if (inlaysText.TryGetValue(inlay.Line, out string? was))
                texts.TryAdd(line, was);
        }

        SetInlays(moved, texts);
    }

    // How wide a hint is drawn, its pill and the room round it
    private float InlayWidth(UISkin skin, float scale, UICodeInlay inlay)
    {
        float digit = FontOf(skin).Measure("0", scale, markup: false).X;
        return FontOf(skin).Measure(inlay.Text, scale, markup: false).X + digit * (INLAY_INSIDE * 2.0f + (inlay.PadLeft ? INLAY_PAD : 0.0f) + (inlay.PadRight ? INLAY_PAD : 0.0f));
    }

    // How many columns a hint takes as the rows are cut, near enough
    private static int InlayColumns(UICodeInlay inlay) => inlay.Text.Length + 1 + (inlay.PadLeft ? 1 : 0) + (inlay.PadRight ? 1 : 0);

    /// <summary>How much room the hints of a line from one column up to (not at) another take.</summary>
    private float InlaysWidth(UISkin skin, float scale, int line, int from, int to)
    {
        if (InlaysOn(line) is not { } on)
            return 0.0f;

        float width = 0.0f;
        foreach (UICodeInlay inlay in on)
        {
            if (inlay.Column >= from && inlay.Column < to)
                width += InlayWidth(skin, scale, inlay);
        }

        return width;
    }

    // How many columns the hints of a line at exactly a column take, for cutting the rows
    private int InlayColumnsAt(int line, int column)
    {
        if (InlaysOn(line) is not { } on)
            return 0;

        int columns = 0;
        foreach (UICodeInlay inlay in on)
        {
            if (inlay.Column == column)
                columns += InlayColumns(inlay);
        }

        return columns;
    }

    // Every hint of a line, for whether the whole line fits on a row
    private int InlayColumnsOf(int line)
    {
        if (InlaysOn(line) is not { } on)
            return 0;

        int columns = 0;
        foreach (UICodeInlay inlay in on)
            columns += InlayColumns(inlay);
        return columns;
    }

    /// <summary>
    /// Draws a piece of a row with the hints in it where they go, each just before the character at its column, and says
    /// where the piece ends. Draw false only makes the room (a gap the colours skip over).
    /// </summary>
    private float DrawWithInlays(UIDrawList list, UISkin skin, float scale, int line, int column, ReadOnlySpan<char> text, float x, float top, Vector4 color, float left, float origin, UISyntaxColors colors, bool draw = true)
    {
        if (InlaysOn(line) is not { } on)
            return draw ? DrawFlowing(list, skin, scale, line, column, text, x, top, color, left, origin) : x + Width(skin, scale, text);

        int at = 0;
        foreach (UICodeInlay inlay in on)
        {
            int into = inlay.Column - column;
            if (into < 0 || into >= text.Length)
                continue;

            if (into > at)
                x = draw ? DrawFlowing(list, skin, scale, line, column + at, text[at..into], x, top, color, left, origin) : x + Width(skin, scale, text[at..into]);
            x = DrawInlay(list, skin, scale, inlay, x, top, colors);
            at = into;
        }

        if (at < text.Length)
            x = draw ? DrawFlowing(list, skin, scale, line, column + at, text[at..], x, top, color, left, origin) : x + Width(skin, scale, text[at..]);
        return x;
    }

    /// <summary>The hints after the end of a line, once the last row of it has been drawn.</summary>
    private float DrawInlaysAtEnd(UIDrawList list, UISkin skin, float scale, int line, int length, float x, float top, UISyntaxColors colors)
    {
        if (InlaysOn(line) is not { } on)
            return x;

        foreach (UICodeInlay inlay in on)
        {
            if (inlay.Column >= length)
                x = DrawInlay(list, skin, scale, inlay, x, top, colors);
        }

        return x;
    }

    // One hint, its pill with the room round it it's padded with
    private float DrawInlay(UIDrawList list, UISkin skin, float scale, UICodeInlay inlay, float x, float top, UISyntaxColors colors)
    {
        float digit = FontOf(skin).Measure("0", scale, markup: false).X;
        DrawPill(list, skin, scale, inlay.Text, x + (inlay.PadLeft ? digit * INLAY_PAD : 0.0f), top, colors);
        return x + InlayWidth(skin, scale, inlay);
    }

    /// <summary>
    /// A faint pill with a hint's words in it in the colour of a comment, from a place along a row. Every hint is drawn
    /// with it, a parameter's name in among the code and the value a line came to after it. Where the pill ends.
    /// </summary>
    private float DrawPill(UIDrawList list, UISkin skin, float scale, string text, float from, float top, UISyntaxColors colors)
    {
        float digit = FontOf(skin).Measure("0", scale, markup: false).X;
        float lineHeight = FontOf(skin).LineHeight * scale;
        float to = from + FontOf(skin).Measure(text, scale, markup: false).X + digit * INLAY_INSIDE * 2.0f;

        var pill = new UIRect(new Vector2(from, top - lineHeight + lineHeight * 0.12f), new Vector2(to, top - lineHeight * 0.12f));
        list.RoundRect(pill, lineHeight * 0.2f, skin.TextColor with { W = 0.07f });
        list.Text(text, new Vector2(from + digit * INLAY_INSIDE, top), scale, colors.Comment with { W = colors.Comment.W * 0.85f }, markup: false);
        return to;
    }

    /// <summary>How much room a hint after the end of a line takes, the gap before it and its pill, for what goes after it.</summary>
    private float EndHintWidth(UISkin skin, float scale, string text, float digit) =>
        digit * (INLAY_END_GAP + INLAY_INSIDE * 2.0f) + FontOf(skin).Measure(text, scale, markup: false).X;
}
