using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Something wrong (or worth a look) at a place in a <see cref="CodeEditor"/>, drawn under the code it is about with a
/// mark in the gutter, and said in the tooltip when the pointer rests on it.
/// </summary>
/// <param name="Line">Which line, counted from 0.</param>
/// <param name="Column">Where on it, counted from 0.</param>
/// <param name="Length">How many characters it is about, at least one is marked.</param>
public readonly record struct UICodeDiagnostic(int Line, int Column, int Length, string Message, bool Warning = false);

/// <summary>Something a <see cref="CodeEditor"/> offers to write where the caret is, a word it knows.</summary>
/// <param name="Text">What it is, which is what is matched against what has been typed so far.</param>
/// <param name="Detail">A few words about it, written fainter next to it.</param>
/// <param name="Insert">What goes in when it is picked, the text itself unless this says otherwise.</param>
public readonly record struct UICompletion(string Text, string Detail = "", string? Insert = null);

/// <summary>What there is to offer at the caret, and from which column of the caret's line what is picked replaces what is there.</summary>
public readonly record struct UICompletionRequest(int Start, IReadOnlyList<UICompletion> Items);

/// <summary>
/// HIDL code to edit, which is what every .hor file is. Coloured in by the language's own lexer the way a
/// <see cref="CodeView"/> is, with its lines numbered, and edited like code. It keeps the indentation, closes brackets
/// and quotes, indents and outdents with tab, comments lines in and out (control with slash), moves and duplicates
/// lines (alt with up and down, control with D), undoes and redoes, finds, and offers to complete what is being typed
/// (as it is typed, and with control and space, see <see cref="CompletionSource"/>). The code is parsed a moment after every
/// change and whatever the parser didn't like is underlined (<see cref="SyntaxError"/>), with whatever the program has
/// to say on top of that (<see cref="Diagnostics"/>).
/// <para>
/// The text and the editing are a <see cref="UITextDocument"/>, the component draws it and hands it the keys and the
/// pointer. It is as big as it is told to be (a size or a fill) and scrolls by itself.
/// </para>
/// </summary>
public class CodeEditor : UIComponent
{
    // How many characters wide the gap between the line numbers and the code is, and the room either side of the code
    private const int GUTTER_GAP = 2;
    private const float TEXT_PAD = 6.0f;

    // How long after the last change the code is parsed again, how often the caret blinks, and the double click
    private const float CHECK_DELAY = 0.35f;
    private const float BLINK = 0.53f;
    private const double DOUBLE_CLICK = 0.4;

    private const float BAR_WIDTH = 6.0f;
    private const int WHEEL_LINES = 3;

    // How many spaces wide a tab is drawn, the font has no tab of its own
    private const int TAB_SPACES = 4;

    // The list of what can be completed, as many rows as it shows at once and the most it holds
    private const int COMPLETION_ROWS = 10;
    private const int MAX_COMPLETIONS = 200;

    // What is offered at the caret, which of it is picked, which is the first in sight, and where what is picked goes in
    private readonly List<UICompletion> completions = [];
    private int completionIndex, completionTop, completionStart;

    // The parts of what is drawn that are counted apart, see UIDrawList.BeginPart. Lines count from 0 on their own
    private const int PART_BACK = -1, PART_OVERLAY = -2, PART_CARET = -3, PART_BAR = -4, PART_GUTTER = 1 << 20;

    private readonly UITextDocument document = new();

    // The colouring of every line, worked out once per change of the text
    private List<HidlSpan>[] spans = [[]];
    private int coloured = -1;

    // Parsing a moment after the last change, and what came of it
    private int checkedVersion = -1;
    private float checkIn;

    private float scrollY, scrollX;
    private float blink;
    private bool revealCaret;

    // Where the pair of the bracket at the caret is, worked out when the caret or the text moves
    private UITextPosition? bracket, bracketPair;
    private (int Version, UITextPosition Caret) bracketFor = (-1, default);

    // What is found, every place of it drawn
    private readonly List<UITextPosition> found = [];
    private string findQuery = string.Empty;
    private int foundVersion = -1;

    // The last press, for telling a double click
    private double lastPress = -1.0;
    private UITextPosition lastPressAt;

    // What the last paint was laid out with, for turning the pointer into a place in the text
    private UISkin? paintedSkin;
    private float paintedScale = 1.0f;

    private IReadOnlyList<UICodeDiagnostic> diagnostics = [];

    /// <summary>The text and everything about editing it.</summary>
    public UITextDocument Document => document;

    /// <summary>The code, its lines ended with '\n'. Setting it starts over, the caret at the top and nothing to undo.</summary>
    public string Text
    {
        get => document.Text;
        set => document.Text = value ?? string.Empty;
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Whether the lines are numbered down the left.</summary>
    public bool LineNumbers { get; set; } = true;

    /// <summary>Whether it can only be read, the caret and the selection still go about it.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Whether the code is parsed after every change and what the parser doesn't like is underlined.</summary>
    public bool CheckSyntax { get; set; } = true;

    /// <summary>Whether the code is coloured as HIDL. Off for any other text, which is all one colour then.</summary>
    public bool Highlight { get; set; } = true;

    /// <summary>
    /// What there is to complete at the caret, asked as it is typed (a word started) and with control and space. Null
    /// for what the editor knows by itself, the language's words and every name already in the code. What comes back is
    /// narrowed down to what matches what is typed between its start and the caret, as the typing goes on.
    /// </summary>
    public Func<CodeEditor, UICompletionRequest?>? CompletionSource { get; set; }

    /// <summary>Whether the list of what can be completed is open.</summary>
    public bool CompletionOpen => IsPopupOpen;

    /// <summary>What the open list offers, in its order.</summary>
    public IReadOnlyList<UICompletion> Completions => completions;

    /// <summary>A colour to fill it with, null for the skin's field colour.</summary>
    public Vector4? Color { get; set; }

    /// <summary>What the parser didn't like the last time it read the code, null for nothing.</summary>
    public UICodeDiagnostic? SyntaxError { get; private set; }

    /// <summary>What the program has to say about the code on top of the parser, underlined the same way.</summary>
    public IReadOnlyList<UICodeDiagnostic> Diagnostics
    {
        get => diagnostics;
        set => diagnostics = value ?? [];
    }

    /// <summary>Called after every change of the text.</summary>
    public Action? OnChanged { get; set; }

    /// <summary>Called after the code has been parsed, with what the parser didn't like (null for nothing).</summary>
    public Action<UICodeDiagnostic?>? OnChecked { get; set; }

    /// <summary>What is being found, every place of it is lit. Empty for nothing.</summary>
    public string FindQuery
    {
        get => findQuery;
        set
        {
            findQuery = value ?? string.Empty;
            foundVersion = -1;
        }
    }

    /// <summary>How many places the find query is found in.</summary>
    public int FoundCount => found.Count;

    public CodeEditor()
    {
        document.Changed += () =>
        {
            checkIn = CHECK_DELAY;
            revealCaret = true;
            OnChanged?.Invoke();
        };
    }

    public CodeEditor(string text)
        : this()
    {
        Text = text;
    }

    protected override bool HitTestVisible => true;
    protected internal override bool Focusable => true;
    protected internal override bool KeepsTab => true;

    /* Finding and going places, for the program around it */

    /// <summary>Selects the next place the find query is, or the one before going back.</summary>
    public bool FindNext(bool forward = true)
    {
        bool any = document.Find(findQuery, forward);
        revealCaret = true;
        return any;
    }

    /// <summary>Swaps every place the find query is for something else, as one step that can be undone.</summary>
    public int ReplaceAll(string with) => ReadOnly ? 0 : document.ReplaceAll(findQuery, with);

    /// <summary>Puts the caret on a line (and a column), in sight.</summary>
    public void GoTo(int line, int column = 0)
    {
        document.MoveTo(new UITextPosition(line, column));
        revealCaret = true;
        centreOnReveal = true;
    }

    // Whether the next reveal puts the caret in the middle rather than just inside the edge, for going somewhere far away
    private bool centreOnReveal;

    /* Measuring */

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float GutterWidth(UISkin skin, float scale)
    {
        if (!LineNumbers)
            return TEXT_PAD;

        // Room for the biggest number there is, every digit of a font is as wide as its zero
        int count = document.LineCount, digits = 1;
        while ((count /= 10) > 0) digits++;
        return skin.Font.Measure("0", scale, markup: false).X * (Math.Max(2, digits) + GUTTER_GAP);
    }

    private float ColumnX(UISkin skin, float scale, int line, int column)
    {
        string text = document.LineAt(line);
        return column <= 0 ? 0.0f : Width(skin, scale, text.AsSpan(0, Math.Min(column, text.Length)));
    }

    /// <summary>How wide a piece of a line is drawn, a tab as wide as so many spaces.</summary>
    private static float Width(UISkin skin, float scale, ReadOnlySpan<char> text)
    {
        float width = 0.0f;
        int tab;
        while ((tab = text.IndexOf('\t')) >= 0)
        {
            width += skin.Font.Measure(text[..tab], scale, markup: false).X + TabWidth(skin, scale);
            text = text[(tab + 1)..];
        }

        return width + skin.Font.Measure(text, scale, markup: false).X;
    }

    private static float TabWidth(UISkin skin, float scale) => skin.Font.Measure(" ", scale, markup: false).X * TAB_SPACES;

    /// <summary>Draws a piece of a line, leaving the room of a tab where there is one.</summary>
    private static float Draw(UIDrawList list, UISkin skin, float scale, ReadOnlySpan<char> text, float x, float top, Vector4 color)
    {
        int tab;
        while ((tab = text.IndexOf('\t')) >= 0)
        {
            if (tab > 0)
            {
                list.Text(text[..tab], new Vector2(x, top), scale, color, markup: false);
                x += skin.Font.Measure(text[..tab], scale, markup: false).X;
            }

            x += TabWidth(skin, scale);
            text = text[(tab + 1)..];
        }

        if (!text.IsEmpty)
        {
            list.Text(text, new Vector2(x, top), scale, color, markup: false);
            x += skin.Font.Measure(text, scale, markup: false).X;
        }

        return x;
    }

    // Every line all one span, for text that isn't coloured as code
    private List<HidlSpan>[] PlainSpans()
    {
        var plain = new List<HidlSpan>[document.LineCount];
        for (int i = 0; i < plain.Length; i++)
        {
            int length = document.LineAt(i).Length;
            plain[i] = length > 0 ? [new HidlSpan(0, length, HidlToken.Name)] : [];
        }

        return plain;
    }

    private UIRect TextArea(UISkin skin, float scale)
    {
        UIRect content = Bounds.Shrink(Padding);
        return new UIRect(new Vector2(content.Min.X + GutterWidth(skin, scale), content.Min.Y), new Vector2(content.Max.X - BAR_WIDTH, content.Max.Y));
    }

    /// <summary>Where in the text a point is, by where it was last drawn.</summary>
    public UITextPosition PositionAt(Vector2 point)
    {
        if (paintedSkin is not { } skin)
            return default;

        float scale = paintedScale;
        float lineHeight = skin.Font.LineHeight * scale;
        UIRect area = TextArea(skin, scale);

        int line = Math.Clamp((int)MathF.Floor((area.Max.Y + scrollY - point.Y) / lineHeight), 0, document.LineCount - 1);
        string text = document.LineAt(line);

        float wanted = point.X - (area.Min.X + TEXT_PAD - scrollX);
        float before = 0.0f;

        for (int i = 1; i <= text.Length; i++)
        {
            float after = Width(skin, scale, text.AsSpan(0, i));
            if (wanted < (before + after) * 0.5f)
                return new UITextPosition(line, i - 1);
            before = after;
        }

        return new UITextPosition(line, text.Length);
    }

    protected override Vector2 Measure(UISkin skin) => new Vector2(480, 320) + Padding.Total;

    /* Updating */

    protected override void Update(float dt)
    {
        blink = (blink + dt) % (BLINK * 2.0f);

        if (coloured != document.Version)
        {
            coloured = document.Version;
            spans = Highlight ? HidlHighlighter.Read(document.Text, document.LineCount) : PlainSpans();
        }

        if (CheckSyntax && checkedVersion != document.Version && (checkIn -= dt) <= 0.0f)
            Check();

        if (bracketFor != (document.Version, document.Caret))
            FindBracket();

        if (foundVersion != document.Version)
        {
            foundVersion = document.Version;
            document.FindAll(findQuery, found);
        }

        if (revealCaret && paintedSkin is { } skin)
            Reveal(skin);

        UpdateTooltip();
    }

    /// <summary>Parses the code now and underlines what the parser didn't like.</summary>
    public void Check()
    {
        checkedVersion = document.Version;
        UICodeDiagnostic? error = null;

        try
        {
            new Parser().ProduceSyntaxTree(Lexer.Tokenize(document.Text));
        }
        catch (ParseException e)
        {
            error = new UICodeDiagnostic(Math.Max(0, e.Line - 1), Math.Max(0, e.Col - 1), Math.Max(1, e.Length), e.Message);
        }
        catch (Exception e)
        {
            error = new UICodeDiagnostic(0, 0, 1, e.Message);
        }

        SyntaxError = error;
        OnChecked?.Invoke(error);
    }

    /// <summary>Helper method to scroll just far enough for the caret to be in sight.</summary>
    private void Reveal(UISkin skin)
    {
        revealCaret = false;

        float scale = ScaleOf(skin);
        float lineHeight = skin.Font.LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        if (area.Height <= 0.0f)
            return;

        UITextPosition caret = document.Caret;
        float top = caret.Line * lineHeight;

        if (centreOnReveal)
        {
            centreOnReveal = false;
            scrollY = top - area.Height * 0.4f;
        }
        else if (top < scrollY)
        {
            scrollY = top;
        }
        else if (top + lineHeight > scrollY + area.Height)
        {
            scrollY = top + lineHeight - area.Height;
        }

        float x = ColumnX(skin, scale, caret.Line, caret.Column);
        float width = area.Width - TEXT_PAD * 2.0f;
        if (x < scrollX)
            scrollX = MathF.Max(0.0f, x - width * 0.25f);
        else if (x > scrollX + width)
            scrollX = x - width * 0.75f;

        ClampScroll(skin, scale);
    }

    private void ClampScroll(UISkin skin, float scale)
    {
        float lineHeight = skin.Font.LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        float most = MathF.Max(0.0f, document.LineCount * lineHeight - area.Height * 0.5f);
        scrollY = Math.Clamp(scrollY, 0.0f, most);
        scrollX = MathF.Max(0.0f, scrollX);
    }

    // The bracket next to the caret and its pair, worked out once a change and not every paint
    private void FindBracket()
    {
        bracketFor = (document.Version, document.Caret);
        bracket = bracketPair = null;

        UITextPosition caret = document.Caret;
        string line = document.LineAt(caret.Line);

        // The one after the caret, or failing that the one before it
        int column = caret.Column < line.Length && IsBracket(line[caret.Column]) ? caret.Column
            : caret.Column > 0 && IsBracket(line[caret.Column - 1]) ? caret.Column - 1 : -1;
        if (column < 0)
            return;

        char open = line[column];
        bool forward = open is '(' or '[' or '{';
        char close = open switch { '(' => ')', '[' => ']', '{' => '}', ')' => '(', ']' => '[', _ => '{' };

        int depth = 0;
        int lineAt = caret.Line, columnAt = column;

        // As far as a few thousand lines, past that it isn't worth the wait
        for (int steps = 0; steps < 200_000; steps++)
        {
            string text = document.LineAt(lineAt);
            if (columnAt >= 0 && columnAt < text.Length)
            {
                char at = text[columnAt];
                if (at == open) depth++;
                else if (at == close && --depth == 0)
                {
                    bracket = new UITextPosition(caret.Line, column);
                    bracketPair = new UITextPosition(lineAt, columnAt);
                    return;
                }
            }

            if (forward)
            {
                if (++columnAt >= text.Length)
                {
                    if (++lineAt >= document.LineCount) return;
                    columnAt = -1;
                }
            }
            else
            {
                if (--columnAt < 0)
                {
                    if (--lineAt < 0) return;
                    columnAt = document.LineAt(lineAt).Length;
                }
            }
        }
    }

    private static bool IsBracket(char character) => character is '(' or ')' or '[' or ']' or '{' or '}';

    // The tooltip says what is wrong where the pointer rests, and nothing anywhere else
    private void UpdateTooltip()
    {
        if (!IsHovered || Module is not { } module || paintedSkin is null)
        {
            if (Tooltip.Length > 0) Tooltip = string.Empty;
            return;
        }

        UITextPosition at = PositionAt(module.ToLocal(module.Compositor.Pointer.Position));
        string said = string.Empty;

        if (SyntaxError is { } error && Covers(error, at))
            said = error.Message;

        foreach (var diagnostic in diagnostics)
        {
            if (said.Length == 0 && Covers(diagnostic, at))
                said = diagnostic.Message;
        }

        if (!ReferenceEquals(said, Tooltip))
            Tooltip = said;
    }

    private static bool Covers(in UICodeDiagnostic diagnostic, UITextPosition at) =>
        at.Line == diagnostic.Line && at.Column >= diagnostic.Column && at.Column <= diagnostic.Column + Math.Max(1, diagnostic.Length);

    /* The keyboard */

    protected internal override void OnTextInput(char character)
    {
        blink = 0.0f;
        revealCaret = true;

        if (CompletionOpen && CompletionKey(character))
            return;

        if (character == UIKeyboard.COMPLETE)
        {
            Complete(typed: false);
            return;
        }

        switch (character)
        {
            // Going about, which is allowed when it can only be read
            case UIKeyboard.LEFT or UIKeyboard.SELECT_LEFT:
                document.MoveHorizontally(-1, character == UIKeyboard.SELECT_LEFT);
                return;
            case UIKeyboard.RIGHT or UIKeyboard.SELECT_RIGHT:
                document.MoveHorizontally(1, character == UIKeyboard.SELECT_RIGHT);
                return;
            case UIKeyboard.UP or UIKeyboard.SELECT_UP:
                document.MoveVertically(-1, character == UIKeyboard.SELECT_UP);
                return;
            case UIKeyboard.DOWN or UIKeyboard.SELECT_DOWN:
                document.MoveVertically(1, character == UIKeyboard.SELECT_DOWN);
                return;
            case UIKeyboard.PAGE_UP or UIKeyboard.SELECT_PAGE_UP:
                document.MoveVertically(-PageLines(), character == UIKeyboard.SELECT_PAGE_UP);
                return;
            case UIKeyboard.PAGE_DOWN or UIKeyboard.SELECT_PAGE_DOWN:
                document.MoveVertically(PageLines(), character == UIKeyboard.SELECT_PAGE_DOWN);
                return;
            case UIKeyboard.WORD_LEFT or UIKeyboard.SELECT_WORD_LEFT:
                document.MoveWord(-1, character == UIKeyboard.SELECT_WORD_LEFT);
                return;
            case UIKeyboard.WORD_RIGHT or UIKeyboard.SELECT_WORD_RIGHT:
                document.MoveWord(1, character == UIKeyboard.SELECT_WORD_RIGHT);
                return;
            case UIKeyboard.HOME or UIKeyboard.SELECT_HOME:
                document.MoveHome(character == UIKeyboard.SELECT_HOME);
                return;
            case UIKeyboard.END or UIKeyboard.SELECT_END:
                document.MoveEnd(character == UIKeyboard.SELECT_END);
                return;
            case UIKeyboard.TEXT_START or UIKeyboard.SELECT_TEXT_START:
                document.MoveToStart(character == UIKeyboard.SELECT_TEXT_START);
                return;
            case UIKeyboard.TEXT_END or UIKeyboard.SELECT_TEXT_END:
                document.MoveToEnd(character == UIKeyboard.SELECT_TEXT_END);
                return;
            case UIKeyboard.SELECT_ALL:
                document.SelectAll();
                return;
            case UIKeyboard.COPY:
                UIKeyboard.Copy(CopiedText());
                return;

            // The focus and what is open are the compositor's
            case UIKeyboard.ESCAPE or UIKeyboard.PASTE:
                return;
        }

        if (ReadOnly)
            return;

        switch (character)
        {
            case UIKeyboard.BACKSPACE: document.Backspace(); break;
            case UIKeyboard.DELETE: document.Delete(); break;
            case UIKeyboard.WORD_BACKSPACE: document.DeleteWord(-1); break;
            case UIKeyboard.WORD_DELETE: document.DeleteWord(1); break;
            case UIKeyboard.ENTER: document.NewLine(); break;
            case UIKeyboard.TAB: document.IndentLines(); break;
            case UIKeyboard.SHIFT_TAB: document.Outdent(); break;
            case UIKeyboard.UNDO: document.Undo(); break;
            case UIKeyboard.REDO: document.Redo(); break;
            case UIKeyboard.TOGGLE_COMMENT: document.ToggleComment(); break;
            case UIKeyboard.DUPLICATE: document.Duplicate(); break;
            case UIKeyboard.MOVE_LINES_UP: document.MoveLines(-1); break;
            case UIKeyboard.MOVE_LINES_DOWN: document.MoveLines(1); break;

            case UIKeyboard.CUT:
                UIKeyboard.Copy(CopiedText());
                if (!document.HasSelection)
                    SelectLine(document.Caret.Line);
                document.Delete();
                break;

            default:
                if (!UIKeyboard.IsEditingKey(character) && !char.IsControl(character))
                {
                    document.Type(character);

                    // A word being typed (or a member, or a key of a text) is offered what it could be
                    if (UITextDocument.IsWordChar(character) || character is '.' or '@')
                        Complete(typed: true);
                    else
                        CloseCompletion();
                }
                break;
        }

        // Deleting goes on narrowing (or widening) the list down, anything else that edits is done with it
        if (CompletionOpen)
        {
            if (character is UIKeyboard.BACKSPACE or UIKeyboard.DELETE)
                Complete(typed: true);
            else if (!UITextDocument.IsWordChar(character) && character is not ('.' or '@'))
                CloseCompletion();
        }
    }

    /* Completing what is typed */

    /// <summary>
    /// Opens (or narrows down) the list of what the word at the caret could be. Typed, it only opens for a word that has
    /// been started and something that isn't it already, asked for (control and space) it opens whatever.
    /// </summary>
    public void Complete(bool typed = false)
    {
        UICompletionRequest? asked = (CompletionSource ?? DefaultCompletions)(this);
        UITextPosition caret = document.Caret;
        completions.Clear();

        if (asked is not { } request || request.Start < 0 || request.Start > caret.Column)
        {
            CloseCompletion();
            return;
        }

        string line = document.LineAt(caret.Line);
        string prefix = line[request.Start..caret.Column];

        // Typed, nothing started is nothing to offer, unless it comes straight after a dot (a member) or an @ (a key)
        bool afterMark = request.Start > 0 && line[request.Start - 1] is '.' or '@';
        if (typed && prefix.Length == 0 && !afterMark)
        {
            CloseCompletion();
            return;
        }

        // What starts with what is typed first, then what has it anywhere in it, each in the order it came
        foreach (var item in request.Items)
        {
            if (completions.Count >= MAX_COMPLETIONS) break;
            if (item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Has(item.Text))
                completions.Add(item);
        }

        foreach (var item in request.Items)
        {
            if (completions.Count >= MAX_COMPLETIONS) break;
            if (prefix.Length > 0 && item.Text.Contains(prefix, StringComparison.OrdinalIgnoreCase) && !item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Has(item.Text))
                completions.Add(item);
        }

        // Nothing to offer, or the one thing there is, is what is there already
        if (completions.Count == 0 || (typed && completions.Count == 1 && completions[0].Text == prefix))
        {
            CloseCompletion();
            return;
        }

        completionStart = request.Start;
        completionIndex = 0;
        completionTop = 0;
        OpenPopup();
    }

    private bool Has(string text)
    {
        foreach (var item in completions)
        {
            if (item.Text == text)
                return true;
        }

        return false;
    }

    /// <summary>Closes the list of what can be completed, if it is open.</summary>
    public void CloseCompletion()
    {
        if (CompletionOpen)
            ClosePopup();
    }

    /// <summary>Puts what is picked in the list where the word being typed is.</summary>
    public void AcceptCompletion()
    {
        if (!CompletionOpen || completionIndex < 0 || completionIndex >= completions.Count)
            return;

        UICompletion picked = completions[completionIndex];
        UITextPosition caret = document.Caret;

        CloseCompletion();
        document.Select(caret with { Column = completionStart }, caret);
        document.Insert(picked.Insert ?? picked.Text);
        revealCaret = true;
    }

    // The keys that go to the list while it is open, true for one it took
    private bool CompletionKey(char character)
    {
        switch (character)
        {
            case UIKeyboard.UP:
                MoveCompletion(-1);
                return true;
            case UIKeyboard.DOWN:
                MoveCompletion(1);
                return true;
            case UIKeyboard.PAGE_UP:
                MoveCompletion(-COMPLETION_ROWS);
                return true;
            case UIKeyboard.PAGE_DOWN:
                MoveCompletion(COMPLETION_ROWS);
                return true;
            case UIKeyboard.ENTER or UIKeyboard.TAB:
                AcceptCompletion();
                return true;
            case UIKeyboard.COMPLETE:
                return true;

            // Going anywhere else is done with the list
            case UIKeyboard.LEFT or UIKeyboard.RIGHT or UIKeyboard.HOME or UIKeyboard.END or UIKeyboard.WORD_LEFT or UIKeyboard.WORD_RIGHT
                or UIKeyboard.TEXT_START or UIKeyboard.TEXT_END or UIKeyboard.SELECT_LEFT or UIKeyboard.SELECT_RIGHT or UIKeyboard.SELECT_UP
                or UIKeyboard.SELECT_DOWN or UIKeyboard.UNDO or UIKeyboard.REDO:
                CloseCompletion();
                return false;
        }

        return false;
    }

    /// <summary>Moves which of the open list of what can be completed is picked, by so many rows.</summary>
    public void MoveCompletion(int by)
    {
        completionIndex = Math.Clamp(completionIndex + by, 0, completions.Count - 1);
        if (completionIndex < completionTop) completionTop = completionIndex;
        if (completionIndex >= completionTop + COMPLETION_ROWS) completionTop = completionIndex - COMPLETION_ROWS + 1;
    }

    // The language's own words and every name in the code, for an editor nobody told what else there is
    private static readonly string[] Keywords = ["let", "const", "func", "if", "else", "while", "do", "for", "in", "return", "break", "continue", "delete", "true", "false", "null", "vec", "include"];

    private static UICompletionRequest? DefaultCompletions(CodeEditor editor)
    {
        UITextDocument doc = editor.Document;
        int start = doc.Caret.Column - doc.WordBeforeCaret().Length;
        var items = new List<UICompletion>();
        var seen = new HashSet<string>();

        foreach (string keyword in Keywords)
        {
            if (seen.Add(keyword))
                items.Add(new UICompletion(keyword, "keyword"));
        }

        foreach (Token token in Lexer.Tokenize(editor.Text, lenient: true))
        {
            if (token.Type == TokenType.Identifier && token.Value.Length > 1 && seen.Add(token.Value))
                items.Add(new UICompletion(token.Value, "in this file"));
        }

        return new UICompletionRequest(start, items);
    }

    // Where the list is, under the start of the word being completed, or over it when there is no room under it
    private UIRect CompletionBounds()
    {
        if (paintedSkin is not { } skin || completions.Count == 0)
            return default;

        float scale = paintedScale;
        float lineHeight = skin.Font.LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        UITextPosition caret = document.Caret;

        float x = area.Min.X + TEXT_PAD - scrollX + ColumnX(skin, scale, caret.Line, completionStart);
        float below = LineTop(area, caret.Line, lineHeight) - lineHeight;

        float widest = 0.0f;
        int last = Math.Min(completions.Count, completionTop + COMPLETION_ROWS);
        for (int i = completionTop; i < last; i++)
        {
            var item = completions[i];
            float text = skin.Font.Measure(item.Text, scale, markup: false).X;
            float detail = item.Detail.Length > 0 ? skin.Font.Measure(item.Detail, scale * 0.85f, markup: false).X + lineHeight : 0.0f;
            widest = MathF.Max(widest, text + detail);
        }

        float width = widest + TEXT_PAD * 4.0f;
        float height = Math.Min(COMPLETION_ROWS, completions.Count) * lineHeight + TEXT_PAD;

        // Under the line unless that runs off the bottom of the module
        float top = below;
        if (Module is { } module && top - height < module.Frame.Min.Y)
            top = LineTop(area, caret.Line, lineHeight) + height;

        return new UIRect(new Vector2(x - TEXT_PAD, top - height), new Vector2(x - TEXT_PAD + width, top));
    }

    protected internal override bool PopupContains(Vector2 point) => CompletionBounds().Contains(point);

    protected internal override void PaintPopup(UIDrawList list)
    {
        UIRect bounds = CompletionBounds();
        if (bounds.IsEmpty || paintedSkin is not { } skin)
            return;

        float scale = paintedScale;
        float lineHeight = skin.Font.LineHeight * scale;
        UISyntaxColors colors = skin.Syntax;

        list.Box(bounds, skin.PanelColor with { W = 1.0f });
        list.Frame(bounds, 1.0f, skin.AccentColor with { W = 0.6f });

        int last = Math.Min(completions.Count, completionTop + COMPLETION_ROWS);
        for (int i = completionTop; i < last; i++)
        {
            list.BeginPart(i);
            float top = bounds.Max.Y - TEXT_PAD * 0.5f - (i - completionTop) * lineHeight;
            var row = new UIRect(new Vector2(bounds.Min.X + 1.0f, top - lineHeight), new Vector2(bounds.Max.X - 1.0f, top));

            if (i == completionIndex)
                list.Rect(row, skin.AccentColor with { W = 0.4f });

            var item = completions[i];
            list.Text(item.Text, new Vector2(bounds.Min.X + TEXT_PAD, top), scale, colors.Name, markup: false);

            if (item.Detail.Length > 0)
            {
                float detail = skin.Font.Measure(item.Detail, scale * 0.85f, markup: false).X;
                list.Text(item.Detail, new Vector2(bounds.Max.X - TEXT_PAD - detail, top - lineHeight * 0.08f), scale * 0.85f, colors.Comment, markup: false);
            }
        }

        list.EndParts();
    }

    protected internal override void OnPaste(string text)
    {
        if (!ReadOnly)
            document.Insert(text);
    }

    // What control with C takes, the selection, or the caret's whole line without one, the way code editors do it
    private string CopiedText() => document.HasSelection ? document.SelectedText : document.LineAt(document.Caret.Line) + "\n";

    private void SelectLine(int line) =>
        document.Select(new UITextPosition(line, 0), line + 1 < document.LineCount ? new UITextPosition(line + 1, 0) : new UITextPosition(line, document.LineAt(line).Length));

    private int PageLines()
    {
        if (paintedSkin is not { } skin)
            return 20;

        float lineHeight = skin.Font.LineHeight * paintedScale;
        return Math.Max(1, (int)(TextArea(skin, paintedScale).Height / lineHeight) - 1);
    }

    /* The pointer */

    private bool draggingBar;

    protected internal override void OnPointerDown(Vector2 point)
    {
        blink = 0.0f;

        // A press on the list of what can be completed picks the row it is on
        if (CompletionOpen && CompletionBounds() is var listed && listed.Contains(point) && paintedSkin is { } listSkin)
        {
            float rowHeight = listSkin.Font.LineHeight * paintedScale;
            int row = completionTop + (int)((listed.Max.Y - TEXT_PAD * 0.5f - point.Y) / rowHeight);
            if (row >= 0 && row < completions.Count)
            {
                completionIndex = row;
                AcceptCompletion();
            }
            return;
        }

        CloseCompletion();

        if (paintedSkin is { } skin && point.X >= TextArea(skin, paintedScale).Max.X)
        {
            draggingBar = true;
            OnPointerDrag(point);
            return;
        }

        UITextPosition at = PositionAt(point);
        double now = Environment.TickCount64 / 1000.0;

        // In the gutter a press is the whole line
        if (paintedSkin is { } painted && point.X < TextArea(painted, paintedScale).Min.X)
        {
            SelectLine(at.Line);
            return;
        }

        if (now - lastPress < DOUBLE_CLICK && at == lastPressAt)
        {
            document.SelectWordAt(at);
            lastPress = -1.0;
            return;
        }

        lastPress = now;
        lastPressAt = at;
        document.MoveTo(at, GameEngine.Instance.Input.Keyboard.Shift);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (draggingBar)
        {
            if (paintedSkin is { } skin)
            {
                float lineHeight = skin.Font.LineHeight * paintedScale;
                UIRect area = TextArea(skin, paintedScale);
                float total = document.LineCount * lineHeight;
                float share = Math.Clamp((area.Max.Y - point.Y) / MathF.Max(1.0f, area.Height), 0.0f, 1.0f);
                scrollY = share * MathF.Max(0.0f, total - area.Height * 0.5f);
            }

            return;
        }

        document.MoveTo(PositionAt(point), selecting: true);
        revealCaret = true;
    }

    protected internal override void OnPointerUp(Vector2 point) => draggingBar = false;

    protected internal override bool OnScroll(float delta)
    {
        if (paintedSkin is not { } skin)
            return false;

        scrollY -= delta * WHEEL_LINES * skin.Font.LineHeight * paintedScale;
        ClampScroll(skin, paintedScale);
        return true;
    }

    /* Painting */

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UISyntaxColors colors = skin.Syntax;
        paintedSkin = skin;

        float scale = ScaleOf(skin);
        paintedScale = scale;

        float lineHeight = skin.Font.LineHeight * scale;
        float digit = skin.Font.Measure("0", scale, markup: false).X;
        UIRect content = Bounds.Shrink(Padding);
        UIRect area = TextArea(skin, scale);
        float left = area.Min.X + TEXT_PAD - scrollX;

        list.BeginPart(PART_BACK);
        list.Box(Bounds, Color ?? (skin.FieldColor.W > 0.0f ? skin.FieldColor : new Vector4(0.0f, 0.0f, 0.0f, 0.35f)));

        int first = Math.Max(0, (int)(scrollY / lineHeight));
        int last = Math.Min(document.LineCount - 1, first + (int)(area.Height / lineHeight) + 1);

        bool focused = IsFocused;
        UITextPosition start = document.SelectionStart, end = document.SelectionEnd;
        Vector4 selection = skin.AccentColor with { W = 0.35f };
        Vector4 band = skin.HoverColor with { W = skin.HoverColor.W * 0.35f };

        list.PushClip(area);

        // What is behind the code, line by line. The caret's line, the selection, what is found, the brackets
        list.BeginPart(PART_OVERLAY);
        for (int line = first; line <= last; line++)
        {
            float top = LineTop(area, line, lineHeight);

            if (focused && !document.HasSelection && line == document.Caret.Line)
                list.Rect(new UIRect(new Vector2(area.Min.X, top - lineHeight), new Vector2(area.Max.X, top)), band);

            if (document.HasSelection && line >= start.Line && line <= end.Line)
            {
                float from = line == start.Line ? ColumnX(skin, scale, line, start.Column) : 0.0f;
                float to = line == end.Line ? ColumnX(skin, scale, line, end.Column) : ColumnX(skin, scale, line, document.LineAt(line).Length) + digit * 0.5f;
                if (to > from)
                    list.Rect(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top)), selection);
            }
        }

        if (findQuery.Length > 0)
        {
            Vector4 lit = skin.HighlightColor with { W = 0.18f };
            for (int i = 0; i < found.Count; i++)
            {
                UITextPosition at = found[i];
                if (at.Line < first || at.Line > last) continue;

                float top = LineTop(area, at.Line, lineHeight);
                float from = ColumnX(skin, scale, at.Line, at.Column);
                float to = ColumnX(skin, scale, at.Line, at.Column + findQuery.Length);
                list.Rect(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top)), lit);
            }
        }

        if (focused && bracket is { } one && bracketPair is { } two)
        {
            PaintBracket(list, skin, scale, area, left, lineHeight, one);
            PaintBracket(list, skin, scale, area, left, lineHeight, two);
        }

        // The code itself, a line at a time, each a part of its own so a line scrolled along is blended with itself
        Span<char> number = stackalloc char[12];
        for (int line = first; line <= last && line < spans.Length; line++)
        {
            list.BeginPart(line);

            float top = LineTop(area, line, lineHeight);
            ReadOnlySpan<char> text = document.LineAt(line);
            float x = left;
            int at = 0;

            foreach (HidlSpan span in spans[line])
            {
                if (span.Start + span.Length > text.Length)
                    break;

                x += Width(skin, scale, text[at..span.Start]);
                x = Draw(list, skin, scale, text.Slice(span.Start, span.Length), x, top, colors.Of(span.Kind));
                at = span.Start + span.Length;
            }
        }

        // What is wrong, a line under it
        list.BeginPart(PART_CARET);
        if (SyntaxError is { } error)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, error, first, last);
        for (int i = 0; i < diagnostics.Count; i++)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, diagnostics[i], first, last);

        // The caret, which blinks, and stays lit while it is moved or typed with
        if (focused && blink < BLINK && document.Caret.Line >= first && document.Caret.Line <= last)
        {
            float top = LineTop(area, document.Caret.Line, lineHeight);
            float x = left + ColumnX(skin, scale, document.Caret.Line, document.Caret.Column);
            list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + MathF.Max(1.0f, MathF.Round(scale * 8.0f)), top)), skin.HighlightColor);
        }

        list.PopClip();

        // The numbers down the side, the caret's lit, and a mark where something is wrong
        if (LineNumbers)
        {
            list.PushClip(new UIRect(content.Min, new Vector2(area.Min.X, content.Max.Y)));
            for (int line = first; line <= last; line++)
            {
                list.BeginPart(PART_GUTTER + line);
                float top = LineTop(area, line, lineHeight);
                if (!(line + 1).TryFormat(number, out int length))
                    continue;

                float right = area.Min.X - digit * GUTTER_GAP * 0.5f;
                Vector4 shade = line == document.Caret.Line ? colors.Name : colors.LineNumber;
                if (IsMarked(line, out bool warning))
                    shade = warning ? new Vector4(1.0f, 0.78f, 0.3f, 1.0f) : new Vector4(1.0f, 0.4f, 0.36f, 1.0f);

                list.Text(number[..length], new Vector2(right - digit * length, top), scale, shade, markup: false);
            }
            list.PopClip();
        }

        // The bar, as long against its track as what is in sight is against all of it
        list.BeginPart(PART_BAR);
        float total = document.LineCount * lineHeight;
        if (total > area.Height)
        {
            UIRect track = new(new Vector2(content.Max.X - BAR_WIDTH, content.Min.Y), content.Max);
            float length = MathF.Max(24.0f, track.Height * area.Height / total);
            float most = MathF.Max(1.0f, total - area.Height * 0.5f);
            float topOfThumb = track.Max.Y - (track.Height - length) * Math.Clamp(scrollY / most, 0.0f, 1.0f);

            list.Rect(track, skin.HoverColor with { W = skin.HoverColor.W * 0.5f });
            list.Rect(new UIRect(new Vector2(track.Min.X, topOfThumb - length), new Vector2(track.Max.X, topOfThumb)), skin.AccentColor with { W = 0.8f });
        }

        list.EndParts();
    }

    private float LineTop(UIRect area, int line, float lineHeight) => area.Max.Y - (line * lineHeight - scrollY);

    private void PaintBracket(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, UITextPosition at)
    {
        float top = LineTop(area, at.Line, lineHeight);
        float from = ColumnX(skin, scale, at.Line, at.Column);
        float to = ColumnX(skin, scale, at.Line, at.Column + 1);
        list.Outline(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top)), 1.0f, skin.HighlightColor with { W = 0.6f });
    }

    private void PaintDiagnostic(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, in UICodeDiagnostic diagnostic, int first, int last)
    {
        if (diagnostic.Line < first || diagnostic.Line > last || diagnostic.Line >= document.LineCount)
            return;

        int length = document.LineAt(diagnostic.Line).Length;
        int column = Math.Min(diagnostic.Column, length);
        int endColumn = Math.Min(length, column + Math.Max(1, diagnostic.Length));

        float top = LineTop(area, diagnostic.Line, lineHeight);
        float from = ColumnX(skin, scale, diagnostic.Line, column);
        float to = MathF.Max(from + lineHeight * 0.5f, ColumnX(skin, scale, diagnostic.Line, endColumn));
        float thickness = MathF.Max(2.0f, MathF.Round(lineHeight * 0.08f));
        Vector4 shade = diagnostic.Warning ? new Vector4(1.0f, 0.78f, 0.3f, 0.9f) : new Vector4(1.0f, 0.36f, 0.32f, 0.95f);

        list.Rect(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top - lineHeight + thickness)), shade);
    }

    // Whether a line has something wrong on it, for its number in the gutter
    private bool IsMarked(int line, out bool warning)
    {
        warning = false;
        if (SyntaxError is { } error && error.Line == line)
            return true;

        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (diagnostics[i].Line == line)
            {
                warning = diagnostics[i].Warning;
                return true;
            }
        }

        return false;
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("line_numbers", () => LineNumbers, value => LineNumbers = value);
        Expose("read_only", () => ReadOnly, value => ReadOnly = value);
        Expose("check_syntax", () => CheckSyntax, value => CheckSyntax = value);
        Expose("highlight", () => Highlight, value => Highlight = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value.W > 0.0f ? value : null);
    }
}
