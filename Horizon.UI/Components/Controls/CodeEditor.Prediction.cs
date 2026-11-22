using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// What a model predicts is typed next, shown before it's taken: edits of the text, none of them made yet. One insertion
/// at the caret is ghost text, written faint after the caret; anything else is a diff, what goes struck through and what
/// comes faint after it.
/// </summary>
/// <param name="Edits">The edits, in the document's coordinates as it is when the prediction is set.</param>
/// <param name="CaretAfter">Where the caret goes once it's taken, in the text as it is then. Null for the end of the last edit.</param>
public readonly record struct UICodePrediction(IReadOnlyList<UITextEdit> Edits, UITextPosition? CaretAfter = null);

// A prediction of what's typed next, shown in among the code until it's taken or let go of. Ghost text goes on the caret's
// line after the caret, the code after the caret drawn moved along by its width (the row's draw split at its column the
// way DrawWithInlays splits at an inlay), its other lines on rows of their own under it that aren't in the text (a row
// kind beside the lens rows, skipped wherever they are). A diff has what goes struck through on the deletion colour and
// what comes faint in the addition colour after it. Nothing of the document changes until Tab takes it, as one undo
// step. The caret, a click and the wrap go by the text alone. Everything drawn is split and kept when it's set, so the
// paint has a null check when there's none and allocates nothing a frame when there is one
public partial class CodeEditor
{
    // A piece of predicted text written in among a line's own, before the character at its column
    private readonly record struct PredictedPiece(int Line, int Column, string Text, bool Ghost);

    // A line of predicted text on a row of its own under the line it hangs from
    private readonly record struct PredictedRow(int Line, string Text, bool Ghost);

    private UICodePrediction? prediction;

    // Its edits in order, with \n for every line ending, whether it's ghost text, and the text's version it was shown for
    private UITextEdit[] predictionEdits = [];
    private bool predictionGhost;
    private int predictionFor = -1;

    // What's drawn of it, the pieces in among the lines in order along them, the rows of their own by line, and how wide
    // each piece is for the font and size they were last measured in
    private PredictedPiece[] predictedPieces = [];
    private PredictedRow[] predictedRows = [];
    private float[] predictedWidths = [];
    private (UIFont? Font, float Scale) predictedMeasured;

    // Goes up whenever the rows of predicted text change, the rows are made again for it
    private int predictionRowsVersion;

    // Offsets in the remaining ghost of closers already inserted by the document, in document order.
    private int[] predictionClosers = [];

    // The expected result of typing one matching character through the normal document input path.
    private (char Character, string Before, string Inserted, UITextPosition Caret)? predictionTyped;

    // Set while the editor makes the edits itself (taking it, or a word of it), the text's change isn't the end of it
    private bool predictionApplying;

    // Whether Tab went to it once already, the next takes it, and whether the editor had the keyboard while it showed
    private bool predictionWentTo, predictionHadFocus;

    // What a row of predicted text paints in, see RowPart
    private const int ROW_PREDICTED = 6;

    /// <summary>What's predicted, null for nothing. Ghost text typed through or taken a word at a time is what's left of it.</summary>
    public UICodePrediction? Prediction => prediction;

    /// <summary>Whether there's a prediction showing.</summary>
    public bool HasPrediction => prediction is not null;

    /// <summary>Whether it's ghost text, one insertion at the caret, rather than a diff.</summary>
    public bool PredictionIsGhost => prediction is not null && predictionGhost;

    /// <summary>The first line it changes, -1 for none. Shown as "Tab to go to line 40" when it's out of sight.</summary>
    public int PredictionLine => prediction is not null && predictionEdits.Length > 0 ? predictionEdits[0].Start.Line : -1;

    /// <summary>Called when it ends, true when it was taken and false when it was let go of (Escape, typing something else).</summary>
    public Action<bool>? OnPredictionEnded;

    /// <summary>Called when a typed character shortens visible ghost text, including a closer stepped over without editing.</summary>
    public Action? OnPredictionTypedThrough;

    /// <summary>Whether Tab takes a prediction, before the snippets and the indent have it. The completion list keeps Tab while it's open.</summary>
    public bool PredictionTakesTab { get; set; } = true;

    /// <summary>
    /// Shows a prediction, over one that was showing (which ends without OnPredictionEnded). Edits that don't fit the text
    /// as it is (past its end, overlapping) are ignored and nothing shows.
    /// </summary>
    public void SetPrediction(UICodePrediction prediction)
    {
        if (ReadOnly || prediction.Edits is not { Count: > 0 } given || !document.CanApplyEdits(given))
            return;

        var edits = new UITextEdit[given.Count];
        for (int i = 0; i < edits.Length; i++)
            edits[i] = given[i] with { Text = given[i].Text.ReplaceLineEndings("\n") };
        Array.Sort(edits, static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        this.prediction = prediction;
        predictionClosers = [];
        predictionWentTo = false;
        predictionHadFocus = IsFocused;
        Show(edits, edits.Length == 1 && edits[0].Start == edits[0].End && edits[0].Start == document.Caret);
    }

    /// <summary>Apply a remote revision and translate any prediction still valid at its mapped positions.</summary>
    public void ApplySharedText(string text, Func<string, string, string, string?> rebase,
        Func<UITextPosition, UITextPosition>? move)
    {
        if (document.Text == text)
            return;
        if (move is null || prediction is not { } existing)
        {
            document.ApplyExternal(text, rebase, move);
            return;
        }

        var mapped = new UITextEdit[predictionEdits.Length];
        var replaced = new string[mapped.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            UITextEdit edit = predictionEdits[i];
            replaced[i] = document.TextBetween(edit.Start, edit.End);
            mapped[i] = edit with { Start = move(edit.Start), End = move(edit.End) };
        }
        UITextPosition? after = existing.CaretAfter is { } caret ? move(caret) : null;
        predictionApplying = true;
        try { document.ApplyExternal(text, rebase, move); }
        finally { predictionApplying = false; }
        if (!document.CanApplyEdits(mapped))
        {
            ClearPrediction();
            return;
        }
        for (int i = 0; i < mapped.Length; i++)
        {
            if (document.TextBetween(mapped[i].Start, mapped[i].End) != replaced[i])
            {
                ClearPrediction();
                return;
            }
        }
        if (predictionGhost && mapped[0].Start != document.Caret)
        {
            ClearPrediction();
            return;
        }
        prediction = new UICodePrediction(mapped, after);
        Show(mapped, predictionGhost);
    }

    /// <summary>Lets go of what's showing, OnPredictionEnded(false) if anything was.</summary>
    public void ClearPrediction() => EndPrediction(taken: false);

    /// <summary>Whether its first row is on screen, true while the editor hasn't been painted to know.</summary>
    public bool PredictionInSight
    {
        get
        {
            if (prediction is null)
                return false;
            if (paintedSkin is not { } skin || rows.Count == 0)
                return true;

            float lineHeight = FontOf(skin).LineHeight * paintedScale;
            UIRect area = TextArea(skin, paintedScale);
            EnsureRows(skin, paintedScale);
            float down = RowOf(predictionEdits[0].Start.Line, predictionEdits[0].Start.Column) * lineHeight - scrollY;
            return down >= -0.5f && down + lineHeight <= area.Height + 0.5f;
        }
    }

    /// <summary>
    /// Takes it, every edit as one undo step, the caret where it said or at the end of the last edit. False when there's
    /// nothing to take or the text can't be edited.
    /// </summary>
    public bool AcceptPrediction()
    {
        if (prediction is not { } taking || ReadOnly)
            return false;

        UITextEdit[] edits = predictionEdits;
        UITextPosition end = EndOfLast(edits);
        bool applied;
        predictionApplying = true;
        try
        {
            applied = predictionGhost && predictionClosers.Length > 0
                ? ApplyGhostPrefix(edits[0])
                : document.ApplyEdits(edits);
        }
        finally
        {
            predictionApplying = false;
        }

        if (!applied)
        {
            ClearPrediction();
            return false;
        }

        document.MoveTo(Clamped(taking.CaretAfter ?? end));
        revealCaret = true;
        EndPrediction(taken: true);
        return true;
    }

    /// <summary>
    /// Ghost text only: the next word of it typed (with the spaces before it), the rest still showing. The last word ends
    /// it as taken. False when there's no ghost text.
    /// </summary>
    public bool AcceptPredictionWord()
    {
        if (prediction is null || !predictionGhost || ReadOnly)
            return false;

        UITextEdit ghost = predictionEdits[0];
        int length = NextWordLength(ghost.Text);
        if (length >= ghost.Text.Length)
            return AcceptPrediction();

        int consumed = 0;
        while (consumed < predictionClosers.Length && predictionClosers[consumed] < length)
            consumed++;

        UITextPosition end = ghost.Start with { Column = ghost.Start.Column + consumed };
        bool applied;
        predictionApplying = true;
        try
        {
            UITextEdit prefix = new(ghost.Start, end, ghost.Text[..length]);
            applied = consumed > 0 ? ApplyGhostPrefix(prefix) : document.ApplyEdits([prefix]);
        }
        finally
        {
            predictionApplying = false;
        }

        if (!applied)
        {
            ClearPrediction();
            return false;
        }

        UITextPosition caret = document.Caret;
        revealCaret = true;
        ShowRest(caret, ghost.Text[length..], AdvanceClosers(consumed, length));
        return true;
    }

    // A prefix already present in the document only advances the caret.
    private bool ApplyGhostPrefix(UITextEdit edit)
    {
        if (edit.End.Column - edit.Start.Column == edit.Text.Length
            && document.LineAt(edit.Start.Line).AsSpan(edit.Start.Column, edit.Text.Length).SequenceEqual(edit.Text.AsSpan()))
        {
            document.MoveTo(edit.End);
            return true;
        }

        return document.ApplyCompletion(edit, []);
    }

    private int[] AdvanceClosers(int consumed, int length)
    {
        int[] remaining = predictionClosers[consumed..];
        for (int i = 0; i < remaining.Length; i++)
            remaining[i] -= length;
        return remaining;
    }

    // Ends it, telling whoever listens if anything was showing
    private void EndPrediction(bool taken)
    {
        if (prediction is null)
            return;

        prediction = null;
        predictionEdits = [];
        predictionClosers = [];
        predictedPieces = [];
        predictedWidths = [];
        predictionTyped = null;
        predictionWentTo = false;
        if (predictedRows.Length > 0)
        {
            predictedRows = [];
            predictionRowsVersion++;
        }

        OnPredictionEnded?.Invoke(taken);
    }

    // What's drawn of the edits worked out, the text after each split into its lines: ghost text's first line goes in
    // after the caret and the rest on rows under it, a diff's single line after what it replaces and more lines on rows
    // under the last line it replaces
    private void Show(UITextEdit[] edits, bool ghost)
    {
        predictionEdits = edits;
        predictionGhost = ghost;
        predictionFor = document.Version;
        predictionTyped = null;

        var pieces = new List<PredictedPiece>();
        var lines = new List<PredictedRow>();
        foreach (UITextEdit edit in edits)
        {
            string[] split = edit.Text.Split('\n');
            if (ghost)
            {
                if (split[0].Length > 0 || predictionClosers.Length > 0)
                    pieces.Add(new PredictedPiece(edit.Start.Line, edit.Start.Column, split[0], Ghost: true));
                for (int i = 1; i < split.Length; i++)
                    lines.Add(new PredictedRow(edit.Start.Line, split[i], Ghost: true));
                continue;
            }

            if (split.Length == 1)
            {
                if (split[0].Length > 0)
                    pieces.Add(new PredictedPiece(edit.End.Line, edit.End.Column, split[0], Ghost: false));
                continue;
            }

            // Whole lines replaced end at the start of the line after them, the new ones go under the last that goes
            int under = edit.End.Column == 0 && edit.End.Line > edit.Start.Line ? edit.End.Line - 1 : edit.End.Line;
            int count = split[^1].Length == 0 ? split.Length - 1 : split.Length;
            for (int i = 0; i < count; i++)
                lines.Add(new PredictedRow(under, split[i], Ghost: false));
        }

        predictedPieces = [.. pieces];
        predictedWidths = new float[predictedPieces.Length];
        predictedMeasured = (null, 0.0f);

        PredictedRow[] made = [.. lines];
        if (made.Length > 0 || predictedRows.Length > 0)
        {
            predictedRows = made;
            predictionRowsVersion++;
        }
    }

    // What's left of ghost text once some of it was typed or taken, the prediction now (where the caret was to go is
    // the end of what's left)
    private void ShowRest(UITextPosition caret, string rest, int[] closers)
    {
        predictionClosers = closers;
        UITextPosition end = caret with { Column = caret.Column + closers.Length };
        UITextEdit[] edits = [new UITextEdit(caret, end, rest)];
        prediction = new UICodePrediction(edits);
        Show(edits, ghost: true);
    }

    // Whether what's predicted is drawn now: it is for this text, and the completion list isn't over it
    private bool PredictionShown => prediction is not null && predictionFor == document.Version && !CompletionOpen;

    // Rebase the ghost before the editor forwards OnChanged.
    private void PredictionTextChanged()
    {
        if (prediction is not null && !predictionApplying)
            FinishPredictionTyping(steppedOver: false);
    }

    private void PreparePredictionTyping(char character)
    {
        CheckPredictionCaret();
        if (prediction is null || !predictionGhost || predictionFor != document.Version
            || predictionEdits[0].Text is not { Length: > 0 } ghost || ghost[0] != character)
            return;

        UITextPosition at = document.Caret;
        string line = document.LineAt(at.Line);
        predictionTyped = (character, line, line.Insert(at.Column, character.ToString()), at with { Column = at.Column + 1 });
    }

    private void FinishPredictionTyping(bool steppedOver)
    {
        if (predictionTyped is not { } typed)
        {
            if (steppedOver)
                CheckPredictionCaret();
            else
                ClearPrediction();
            return;
        }

        predictionTyped = null;
        UITextPosition caret = document.Caret;
        if (caret != typed.Caret || document.HasSelection || document.HasExtraCarets)
        {
            ClearPrediction();
            return;
        }

        string line = document.LineAt(caret.Line);
        string rest = predictionEdits[0].Text[1..];
        int consumed = 0, added = -1;
        if (steppedOver && line == typed.Before && line[caret.Column - 1] == typed.Character)
        {
            if (predictionClosers.Length > 0)
                consumed = 1;
        }
        else if (!steppedOver && line == typed.Inserted)
        {
            if (predictionClosers.Length > 0 && predictionClosers[0] == 0)
            {
                ClearPrediction();
                return;
            }
        }
        else if (!steppedOver && line.Length == typed.Inserted.Length + 1
            && line.AsSpan(0, caret.Column).SequenceEqual(typed.Inserted.AsSpan(0, caret.Column))
            && line.AsSpan(caret.Column + 1).SequenceEqual(typed.Inserted.AsSpan(caret.Column)))
        {
            int before = predictionClosers.Length == 0 ? rest.Length : predictionClosers[0] - 1;
            added = PredictedCloser(rest, before, typed.Character, line[caret.Column]);
            if (added < 0 && predictionClosers.Length > 0)
            {
                ClearPrediction();
                return;
            }
        }
        else
        {
            ClearPrediction();
            return;
        }

        if (rest.Length == 0)
        {
            EndPrediction(taken: true);
            return;
        }

        int[] closers = AdvanceClosers(consumed, 1);
        ShowRest(caret, rest, added < 0 ? closers : [added, .. closers]);
        OnPredictionTypedThrough?.Invoke();
    }

    // Find the mate of the typed opener before any already matched enclosing closer.
    private static int PredictedCloser(string rest, int before, char opening, char closing)
    {
        int nested = 0;
        char quote = opening == closing ? opening : '\0';
        for (int i = 0; i < before; i++)
        {
            char character = rest[i];
            if (quote != '\0')
            {
                if (character == '\\')
                    i++;
                else if (character == quote)
                {
                    if (quote == closing)
                        return i;
                    quote = '\0';
                }
                continue;
            }

            if (character is '"' or '\'')
                quote = character;
            else if (character == closing)
            {
                if (nested == 0)
                    return i;
                nested--;
            }
            else if (character == opening)
                nested++;
        }

        return -1;
    }

    // Every tick, ghost text goes once the caret leaves its place, and any prediction once the editor loses the keyboard
    private void UpdatePrediction()
    {
        if (prediction is null)
            return;

        if (IsFocused)
            predictionHadFocus = true;
        else if (predictionHadFocus)
        {
            ClearPrediction();
            return;
        }

        CheckPredictionCaret();
    }

    // Ghost text is at the caret, a caret gone from there (or a selection made, or more carets) lets go of it
    private void CheckPredictionCaret()
    {
        if (prediction is not null && predictionGhost
            && (document.Caret != predictionEdits[0].Start || document.HasSelection || document.HasExtraCarets))
            ClearPrediction();
    }

    /// <summary>
    /// The keys a prediction takes before the completion list and everything else: Tab takes it (or goes to it when it's
    /// out of sight), Ctrl+Right a word of ghost text, and Escape lets go of it. True when the key was its.
    /// </summary>
    private bool PredictionKey(char character)
    {
        CheckPredictionCaret();
        if (prediction is null)
            return false;

        bool plain = !ReadOnly && !document.HasSelection && !document.HasExtraCarets;
        switch (character)
        {
            case UIKeyboard.ESCAPE when !CompletionOpen:
                ClearPrediction();
                return true;

            case UIKeyboard.TAB when !CompletionOpen && PredictionTakesTab && plain:
                if (PredictionInSight || (predictionWentTo && document.Caret.Line == PredictionLine))
                    AcceptPrediction();
                else
                    GoToPrediction();
                return true;

            case UIKeyboard.WORD_RIGHT when !CompletionOpen && predictionGhost && plain:
                return AcceptPredictionWord();
        }

        return false;
    }

    // The first Tab with a prediction out of sight goes to it, the next takes it
    private void GoToPrediction()
    {
        document.MoveTo(predictionEdits[0].Start);
        revealCaret = true;
        centreOnReveal = true;
        predictionWentTo = true;
    }

    // How much of ghost text the next word is: the spaces and line breaks before it, then a word or a run of anything else
    private static int NextWordLength(string text)
    {
        int at = 0;
        while (at < text.Length && char.IsWhiteSpace(text[at]))
            at++;

        if (at < text.Length && IsWordChar(text[at]))
        {
            while (at < text.Length && IsWordChar(text[at]))
                at++;
        }
        else
        {
            while (at < text.Length && !char.IsWhiteSpace(text[at]) && !IsWordChar(text[at]))
                at++;
        }

        return Math.Max(at, Math.Min(1, text.Length));
    }

    // Where the end of the last edit is once they're all made, every edit before it moving it along
    private static UITextPosition EndOfLast(UITextEdit[] edits)
    {
        UITextEdit last = edits[^1];
        UITextPosition at = last.Start;
        for (int i = edits.Length - 2; i >= 0; i--)
            at = MovedBy(at, edits[i]);
        return After(at, last.Text);
    }

    // A place at or after an edit, where it is once the edit is made
    private static UITextPosition MovedBy(UITextPosition at, UITextEdit edit)
    {
        UITextPosition end = After(edit.Start, edit.Text);
        if (at.Line == edit.End.Line)
            return new UITextPosition(end.Line, end.Column + at.Column - edit.End.Column);
        return at with { Line = at.Line + end.Line - edit.End.Line };
    }

    // Where text put in at a place ends
    private static UITextPosition After(UITextPosition at, string text)
    {
        int breaks = 0, lastBreak = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                breaks++;
                lastBreak = i;
            }
        }

        return breaks == 0 ? at with { Column = at.Column + text.Length } : new UITextPosition(at.Line + breaks, text.Length - lastBreak - 1);
    }

    private UITextPosition Clamped(UITextPosition at)
    {
        int line = Math.Clamp(at.Line, 0, document.LineCount - 1);
        return new UITextPosition(line, Math.Clamp(at.Column, 0, document.LineAt(line).Length));
    }

    /* Rows */

    private int? VisiblePredictionRowsVersion => predictedRows.Length > 0 && PredictionShown ? predictionRowsVersion : null;

    // The rows of predicted text under a line, after its own, walking the rows in order with the line
    private void AddPredictedRows(int line, ref int next)
    {
        while (next < predictedRows.Length && predictedRows[next].Line < line)
            next++;
        for (; next < predictedRows.Length && predictedRows[next].Line == line; next++)
        {
            int end = document.LineAt(line).Length;
            rows.Add(new CodeRow(line, end, end, 0.0f, Predicted: next + 1));
        }
    }

    /* Painting */

    // Every piece measured once for the font and size the code's drawn in
    private void MeasurePredicted(UISkin skin, float scale)
    {
        UIFont font = FontOf(skin);
        if (ReferenceEquals(predictedMeasured.Font, font) && predictedMeasured.Scale == scale)
            return;

        predictedMeasured = (font, scale);
        for (int i = 0; i < predictedPieces.Length; i++)
            predictedWidths[i] = Width(skin, scale, predictedPieces[i].Text);
    }

    // The faint colour ghost text is written in, the line numbers' the way the lenses over the lines are, and the colours
    // a diff's coming and going text have, the gutter's change marks'
    private static Vector4 GhostColour(UISyntaxColors colors) => Vector4.Lerp(colors.LineNumber, colors.Name, 0.2f);

    private static Vector4 AddedColour(UISkin skin) => ChangeColour(UILineChangeKind.Added, skin) with { W = 0.8f };

    /// <summary>
    /// Draws a piece of a row with the predicted text that goes in among it where it goes, each piece just before the
    /// character at its column, and says where the piece ends. Draw false only makes the room, the predicted text is
    /// drawn either way (it isn't the code's, it's never a gap the colours skip over).
    /// </summary>
    private float DrawWithInlays(UIDrawList list, UISkin skin, float scale, int line, int column, ReadOnlySpan<char> text, float x, float top, Vector4 color, float left, float origin, UISyntaxColors colors, bool draw = true)
    {
        if (predictedPieces.Length == 0 || !PredictionShown)
            return DrawWithHints(list, skin, scale, line, column, text, x, top, color, left, origin, colors, draw);

        MeasurePredicted(skin, scale);
        UITextEdit edit = predictionEdits[0];
        bool replacesClosers = predictionGhost && predictionClosers.Length > 0 && edit.Start.Line == line;
        int hiddenUntil = replacesClosers ? Math.Clamp(edit.End.Column - column, 0, text.Length) : 0;
        int at = replacesClosers && column > edit.Start.Column ? hiddenUntil : 0;
        for (int i = 0; i < predictedPieces.Length; i++)
        {
            PredictedPiece piece = predictedPieces[i];
            int into = piece.Column - column;
            if (piece.Line != line || into < at || into >= text.Length)
                continue;

            if (into > at)
                x = DrawWithHints(list, skin, scale, line, column + at, text[at..into], x, top, color, left, origin, colors, draw);
            x = DrawPredicted(list, skin, scale, i, x, top, colors);
            at = replacesClosers ? hiddenUntil : into;
        }

        return at < text.Length || text.IsEmpty ? DrawWithHints(list, skin, scale, line, column + at, text[at..], x, top, color, left, origin, colors, draw) : x;
    }

    // The predicted text after the end of a line, once the last row of it has been drawn
    private float DrawPredictedAtEnd(UIDrawList list, UISkin skin, float scale, int line, int length, float x, float top, UISyntaxColors colors)
    {
        if (predictedPieces.Length == 0 || !PredictionShown)
            return x;

        MeasurePredicted(skin, scale);
        for (int i = 0; i < predictedPieces.Length; i++)
        {
            if (predictedPieces[i].Line == line && predictedPieces[i].Column >= length)
                x = DrawPredicted(list, skin, scale, i, x, top, colors);
        }

        return x;
    }

    // One piece, faint, and where it ends
    private float DrawPredicted(UIDrawList list, UISkin skin, float scale, int index, float x, float top, UISyntaxColors colors)
    {
        PredictedPiece piece = predictedPieces[index];
        if (piece.Text.Length > 0)
            Draw(list, skin, scale, piece.Text, x, top, piece.Ghost ? GhostColour(colors) : AddedColour(skin));
        return x + predictedWidths[index];
    }

    // A row of predicted text of its own, from the left of the code (the text brings its own indentation)
    private void PaintPredictedRow(UIDrawList list, UISkin skin, float scale, in CodeRow row, float top, float left, UISyntaxColors colors)
    {
        int index = row.Predicted - 1;
        if (index < 0 || index >= predictedRows.Length || !PredictionShown)
            return;

        list.BeginPart(RowPart(ROW_PREDICTED, row));
        PredictedRow shown = predictedRows[index];
        Draw(list, skin, scale, shown.Text, left, top, shown.Ghost ? GhostColour(colors) : AddedColour(skin));
    }

    /// <summary>
    /// Helper method to paint what a diff takes away, the deletion colour behind it and a line through it, a box for each
    /// row it's on. A line break it takes is a digit's width after its line.
    /// </summary>
    private void PaintPredictionStruck(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, float digit, int firstLine, int lastLine)
    {
        if (predictionGhost || !PredictionShown)
            return;

        MeasurePredicted(skin, scale);
        Vector4 behind = ChangeColour(UILineChangeKind.Removed, skin) with { W = 0.2f };
        Vector4 through = ChangeColour(UILineChangeKind.Removed, skin) with { W = 0.85f };
        for (int e = 0; e < predictionEdits.Length; e++)
        {
            UITextEdit edit = predictionEdits[e];
            if (edit.Start == edit.End || edit.End.Line < firstLine || edit.Start.Line > lastLine)
                continue;

            for (int line = Math.Max(edit.Start.Line, firstLine); line <= edit.End.Line && line <= lastLine && line < document.LineCount; line++)
            {
                if (IsHidden(line, out _))
                    continue;

                string text = document.LineAt(line);
                int from = line == edit.Start.Line ? Math.Min(edit.Start.Column, text.Length) : 0;
                int to = line == edit.End.Line ? Math.Min(edit.End.Column, text.Length) : text.Length;
                bool lineBreak = line < edit.End.Line;
                if (from >= to)
                {
                    // Nothing of the line but its break, a box the width of a digit where the break is
                    if (lineBreak)
                    {
                        float at = ColumnX(skin, scale, line, from) + PredictedShift(line, from);
                        StruckBox(list, skin, scale, area, left, lineHeight, line, from, at, at + digit, behind, through);
                    }
                    continue;
                }

                while (from < to)
                {
                    // To the end of the row it starts on, the next row's start when the line is wrapped there
                    int row = RowOf(line, from);
                    int rowEnd = row + 1 < rows.Count && rows[row + 1].Line == line && !rows[row + 1].Virtual ? rows[row + 1].Start : int.MaxValue;
                    int end = Math.Min(to, rowEnd);
                    float x0 = ColumnX(skin, scale, line, from) + PredictedShift(line, from);
                    float x1 = ColumnX(skin, scale, line, end - 1) + PredictedShift(line, end - 1) + Width(skin, scale, text.AsSpan(end - 1, 1));
                    if (end == to && lineBreak)
                        x1 += digit;
                    StruckBox(list, skin, scale, area, left, lineHeight, line, from, x0, x1, behind, through);
                    from = end;
                }
            }
        }
    }

    // One box of what goes on the row a place is on, from one x along it to another, with the line through it
    private void StruckBox(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int line, int column, float from, float to, Vector4 behind, Vector4 through)
    {
        float top = TopOf(area, line, column, lineHeight);
        list.Rect(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top)), behind);
        Strike(list, left + from, left + to, StrikeY(skin, scale, top, lineHeight), through);
    }

    // How far the predicted text written in among a line before a column (and at it) pushes that column along
    private float PredictedShift(int line, int column)
    {
        float shift = 0.0f;
        for (int i = 0; i < predictedPieces.Length; i++)
        {
            if (predictedPieces[i].Line == line && predictedPieces[i].Column <= column)
                shift += predictedWidths[i];
        }

        return shift;
    }
}
