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

/// <summary>How a function is called, said over a call being typed, the piece from Start for Length (the argument the caret is on) in bold.</summary>
public readonly record struct UISignature(string Text, int Start, int Length);

/// <summary>A few words written faintly after the end of a line, the value a let came to in Hex (Light Table's inline results).</summary>
public readonly record struct UICodeHint(int Line, string Text);

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
public partial class CodeEditor : UIComponent
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
    private const int PART_BACK = -1, PART_OVERLAY = -2, PART_CARET = -3, PART_BAR = -4, PART_SWATCHES = -5, PART_BACKDROP = -6, PART_STICKY = -7, PART_SIGNATURE = -8, PART_ART = -9, PART_GUTTER = 1 << 20;

    private readonly UITextDocument document = new();

    // The colouring of every line, and the colours written in it. Worked out for the whole file, 1.7 ms and a megabyte for
    // two thousand lines, on a thread of the pool once the text has been left alone RECOLOUR_DELAY (a new text straight
    // away, on the editor's). Until it comes back a line that isn't what was coloured is coloured on its own, from where
    // the last colouring found it starts (FreshColours), it was drawn plain for a tenth of a second. What colours it is
    // the highlighter, HIDL unless the file is another language (CodeLanguages)
    private const float RECOLOUR_DELAY = 0.05f;
    private List<SyntaxSpan>[] spans = [[]];
    private string[] colouredLines = [];
    private CodeLineStart[] lineStarts = [CodeLineStart.First];
    private ICodeHighlighter highlighter = HidlHighlighter.Instance;
    private readonly List<HidlSwatch> swatches = [];
    private int coloured = -1, changeSeen = -1;
    private float sinceChange;
    private bool recolourNow = true;

    // The colouring going on off the editor's thread, and the lines coloured on their own while it does, by their text
    private sealed record Colouring(int Version, List<SyntaxSpan>[] Spans, string[] Lines, List<HidlSwatch> Swatches, CodeLineStart[] Starts, List<HidlArt> Art);

    // The art named in the code, found with the colours, where the last paint put each of it (nothing for what it couldn't
    // draw), which of it the pointer is on, and where the pictures named in it are on disk by the name, asked once a name
    private readonly List<HidlArt> art = [];
    private UIRect[] artBoxes = [];
    private int hoveredArt = -1;
    private readonly Dictionary<string, string?> artFiles = new(StringComparer.Ordinal);
    private Func<string, string?>? artPath;
    private Task<Colouring>? colouring;
    private readonly Dictionary<string, List<SyntaxSpan>> fresh = new(StringComparer.Ordinal);
    private const int MAX_FRESH = 256;

    // The parse going on off the editor's thread, what it found and for which version of the text
    private sealed record Checked(int Version, UICodeDiagnostic? Error, List<UICodeDiagnostic> More);
    private Task<Checked>? checking;

    // Every other place the word under the caret is, lit faintly, worked out when the caret or the text moves
    private readonly List<UITextPosition> occurrences = [];
    private int occurrenceLength;
    private (int Version, UITextPosition Caret) occurrencesFor = (-1, default);
    private const int MAX_OCCURRENCES = 500;

    // What the hover hint was last asked about, so it is asked once for as long as the pointer stays on one word
    private (int Version, int Line, int Start) hoverFor = (-1, -1, -1);
    private string hoverText = string.Empty;

    // Parsing a moment after the last change, and what came of it
    private int checkedVersion = -1;
    private float checkIn;

    private float scrollY, scrollX;

    // Where the wheel wants the view to be, which it eases to (null while it is where it's wanted). The wheel used to move
    // it three lines in one tick, and the frames between two ticks blend the rows that were in sight both times, so the
    // rows that came into sight sat over the ones still sliding past for a frame, the text flickered as it scrolled.
    // A few pixels a tick nothing comes in over anything
    private float? scrollGoal;

    // How quickly the view gets to where the wheel wants it, a share of the way left every second (most of it in a tenth)
    private const float SCROLL_EASE = 22.0f;
    private float blink;
    private bool revealCaret;

    // Where the pair of the bracket at the caret is, worked out when the caret or the text moves
    private UITextPosition? bracket, bracketPair;
    private (int Version, UITextPosition Caret) bracketFor = (-1, default);

    // What is found, every place of it drawn, and how it's looked for
    private readonly List<UITextMatch> found = [];
    private string findQuery = string.Empty;
    private UIFindOptions findOptions;
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
        set
        {
            document.Text = value ?? string.Empty;
            recolourNow = true;
        }
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Whether the lines are numbered down the left.</summary>
    public bool LineNumbers { get; set; } = true;

    /// <summary>Whether it can only be read, the caret and the selection still go about it.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Whether the code is parsed after every change and what the parser doesn't like is underlined.</summary>
    public bool CheckSyntax { get; set; } = true;

    /// <summary>Whether the code is coloured as code. Off for plain text, which is all one colour then.</summary>
    public bool Highlight { get; set; } = true;

    /// <summary>
    /// What colours the code, HIDL through its grammar and its lexer unless told another language
    /// (<see cref="CodeLanguages.ForFile"/>). Null is plain text, the same as <see cref="Highlight"/> off.
    /// </summary>
    public ICodeHighlighter? Highlighter
    {
        get => Highlight ? highlighter : null;
        set
        {
            Highlight = value is not null;
            if (value is not null && !ReferenceEquals(value, highlighter))
            {
                highlighter = value;
                coloured = -1;
                recolourNow = true;
            }
        }
    }

    /// <summary>
    /// What there is to complete at the caret, asked as it is typed (a word started) and with control and space. Null
    /// for what the editor knows by itself, the language's words and every name already in the code. What comes back is
    /// narrowed down to what matches what is typed between its start and the caret, as the typing goes on.
    /// </summary>
    public Func<CodeEditor, UICompletionRequest?>? CompletionSource { get; set; }

    /// <summary>
    /// Whether the last asking of <see cref="CompletionSource"/> was for something typed rather than asked for (control
    /// and space), for a source that answers later to open the list again the same way (<see cref="Complete"/>).
    /// </summary>
    public bool CompletionTyped { get; private set; }

    /// <summary>Whether the list of what can be completed is open.</summary>
    public bool CompletionOpen => IsPopupOpen;

    /// <summary>What the open list offers, in its order.</summary>
    public IReadOnlyList<UICompletion> Completions => completions;

    /// <summary>A colour to fill it with, null for the skin's field colour.</summary>
    public Vector4? Color { get; set; }

    /// <summary>What the parser didn't like the last time it read the code, null for nothing.</summary>
    public UICodeDiagnostic? SyntaxError { get; private set; }

    // What else the parser didn't like after the first, it goes on after a mistake
    private readonly List<UICodeDiagnostic> moreErrors = [];

    /// <summary>Everything the parser didn't like, the first of it <see cref="SyntaxError"/>.</summary>
    public IReadOnlyList<UICodeDiagnostic> SyntaxErrors => SyntaxError is { } first ? [first, .. moreErrors] : [];

    /// <summary>What the program has to say about the code on top of the parser, underlined the same way.</summary>
    public IReadOnlyList<UICodeDiagnostic> Diagnostics
    {
        get => diagnostics;
        set => diagnostics = value ?? [];
    }

    /// <summary>
    /// What to say about the word the pointer rests on, markdown (what it is, where it was made, what it does), null
    /// or empty for nothing. Asked once for as long as the pointer stays on the same word. What is wrong there is said
    /// first.
    /// </summary>
    public Func<CodeEditor, UITextPosition, string?>? HoverSource { get; set; }

    /// <summary>
    /// How the function being called at the caret is called, asked with its name (dotted, math.floor) and which argument the
    /// caret is on from 0 whenever the caret goes into or along a call, null for nothing to say. Drawn over the call.
    /// </summary>
    public Func<string, int, UISignature?>? SignatureSource { get; set; }

    /// <summary>What the call at the caret was said to be, null when the caret isn't in one or nobody had anything to say.</summary>
    public UISignature? Signature => signature;

    /// <summary>A few words after the end of some lines, faint, by the line. Empty for none.</summary>
    public IReadOnlyList<UICodeHint> InlineHints
    {
        get => inlineHints;
        set => inlineHints = value ?? [];
    }

    private IReadOnlyList<UICodeHint> inlineHints = [];

    /// <summary>Called when the swatch of a colour written in the code is clicked, to pick another (see <see cref="HidlSwatch"/>).</summary>
    public Action<HidlSwatch>? OnSwatchPressed { get; set; }

    /// <summary>The colours written in the code (color: vec(...)), each with a swatch at the end of its line.</summary>
    public IReadOnlyList<HidlSwatch> Swatches => swatches;

    /// <summary>
    /// What the code at a place was last coloured as, for a test. Null for a line the colours haven't caught up with yet,
    /// and a plain name for a place on a coloured line that nothing was said about (whitespace).
    /// </summary>
    public SyntaxKind? KindAt(int line, int column)
    {
        if (line < 0 || line >= spans.Length || line >= colouredLines.Length || colouredLines[line] != document.LineAt(line))
            return null;

        foreach (SyntaxSpan span in spans[line])
        {
            if (span.Start <= column && column < span.Start + span.Length)
                return span.Kind;
        }

        return SyntaxKind.Name;
    }

    /// <summary>
    /// The art named in the code, Codicons (<c>$(play)</c>, <c>icon: "play"</c>), the skin's icons (<c>[icon:pad_a]</c>) and
    /// sprites (<c>sprite: "button"</c>), and pictures (a text that is the path of a .png or an Aseprite file), each drawn
    /// small at the end of its line and big while the pointer is on it.
    /// </summary>
    public IReadOnlyList<HidlArt> Art => art;

    /// <summary>Whether the art named in the code is drawn at the end of its line.</summary>
    public bool ShowArt { get; set; } = true;

    /// <summary>
    /// Where a picture named in the code is, a path that can be opened, or null for nowhere. Asked once for every name
    /// (until it is set again), from the editor's thread. Null to take the names as they are, from the working folder,
    /// which is how a layout names its pictures.
    /// </summary>
    public Func<string, string?>? ArtPath
    {
        get => artPath;
        set
        {
            artPath = value;
            artFiles.Clear();
        }
    }

    /// <summary>
    /// The skin the icons and sprites the code names are looked for in first, the skin of the game or the layout being
    /// written rather than the one the editor wears (an editor's flat skin has no gamepad buttons). Asked every paint, so
    /// it follows whatever the program changes it to. Null, or null coming back, for the editor's own alone.
    /// </summary>
    public Func<UISkin?>? ArtSkin { get; set; }

    /// <summary>Which of <see cref="Art"/> the pointer is on, -1 for none.</summary>
    public int HoveredArt => hoveredArt;

    /// <summary>Where the last paint drew a piece of <see cref="Art"/>, nothing for one it had no picture of.</summary>
    public UIRect ArtBounds(int index) => index >= 0 && index < artBoxes.Length ? artBoxes[index] : default;

    /// <summary>
    /// The font the code is written in, null for the skin's. Everything the editor writes is in it, the numbers down the
    /// side and the list of what can be completed as well, the rest of the UI keeps the skin's (see UIDrawList.PushFont).
    /// </summary>
    public UIFont? Font { get; set; }

    // The font the code is measured and written in
    private UIFont FontOf(UISkin skin) => Font ?? skin.Font;

    /// <summary>Whether guides are drawn down the indentation.</summary>
    public bool IndentGuides { get; set; } = true;

    /// <summary>
    /// A picture drawn behind the code in its bottom right corner, the way an IDE has a background picture (somebody's
    /// favourite character, usually). Null for none. Turning it is the program's business, the editor draws it upright.
    /// </summary>
    public Horizon.Graphics.Texture? Backdrop { get; set; }

    /// <summary>How tall the picture is drawn, as a share of the window's height, so it keeps its size against the window as that's resized.</summary>
    public float BackdropScale { get; set; } = 0.5f;

    /// <summary>
    /// How far the picture is from the bottom right corner, along to the left and up, as shares of the window's width
    /// and height.
    /// </summary>
    public Vector2 BackdropOffset { get; set; }

    /// <summary>How much of the picture shows through, the code is written over it.</summary>
    public float BackdropOpacity { get; set; } = 0.3f;

    /// <summary>
    /// The shape of what is drawn in the <see cref="Backdrop"/> (<see cref="UIWrapShape.FromFile"/> of the same picture),
    /// which wrapped code runs round instead of over, null for code that is written over the picture as ever. Only while
    /// <see cref="WordWrap"/> is on, a line that doesn't wrap can't go round anything.
    /// </summary>
    public UIWrapShape? BackdropShape { get; set; }

    /// <summary>How far the wrapped code keeps off the <see cref="BackdropShape"/>, in the UI's pixels, round every edge of it.</summary>
    public float BackdropMargin { get; set; } = 8.0f;

    /// <summary>
    /// Shapes of the program's own that wrapped code runs round, placed in the text area from its bottom left corner
    /// (the UI's y going up). They stay where they are as the code scrolls, the way the picture does, so the code
    /// flows round them anew as it goes by. Only while <see cref="WordWrap"/> is on.
    /// </summary>
    public List<UIWrapArea> WrapAreas { get; } = [];

    /// <summary>Where the <see cref="Backdrop"/> is drawn in the text area, its bottom right corner moved by the offset.</summary>
    private UIRect BackdropBounds(UIRect area, Horizon.Graphics.Texture picture) =>
        PlaceBackdrop(area, Module?.Frame ?? area, picture, BackdropScale, BackdropOffset);

    /// <summary>
    /// Where a backdrop goes in an area, its bottom right corner moved in by the offset, the size and the offset both
    /// shares of the window (its height for the size, the picture keeping its shape) rather than of the picture's pixels.
    /// </summary>
    internal static UIRect PlaceBackdrop(UIRect area, UIRect window, Horizon.Graphics.Texture picture, float scale, Vector2 offset)
    {
        float height = window.Height * scale;
        Vector2 drawn = new(picture.Height > 0 ? height * picture.Width / picture.Height : 0.0f, height);
        Vector2 corner = new(area.Max.X - offset.X * window.Width, area.Min.Y + offset.Y * window.Height);
        return new UIRect(corner - new Vector2(drawn.X, 0.0f), corner + new Vector2(0.0f, drawn.Y));
    }

    /// <summary>
    /// Whether a thin line in the accent runs along its top while it has the keyboard, so which of several things taking
    /// keys has them can be seen (VS Code's focused editor group).
    /// </summary>
    public bool FocusLine { get; set; }

    /// <summary>
    /// The lines with a breakpoint on them, a red dot each in the gutter where the marks go. The program keeps the set
    /// (a debugger's), the editor only shows it. Null for none.
    /// </summary>
    public IReadOnlySet<int>? Breakpoints { get; set; }

    /// <summary>
    /// A press in the gutter's strip for the marks, given the line pressed, a breakpoint put down or taken up. Set, a dot
    /// shows faintly under the pointer there. Null and a press in the gutter selects the line, as it always did.
    /// </summary>
    public Action<int>? OnGutterPressed { get; set; }

    /// <summary>The line a debugger stopped on, lit right across and an arrow in the gutter, -1 for none.</summary>
    public int ExecutionLine { get; set; } = -1;

    /// <summary>Every place the word under the caret is, which are lit faintly.</summary>
    public IReadOnlyList<UITextPosition> Occurrences => occurrences;

    /// <summary>
    /// Called with the notches of the wheel turned while control is held, to zoom the code (away from the user is in).
    /// Null, the wheel scrolls whatever is held.
    /// </summary>
    public Action<float>? OnZoom { get; set; }

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

    /// <summary>How it's looked for, the case, whole words, a regex. Every place it's found is lit.</summary>
    public UIFindOptions FindOptions
    {
        get => findOptions;
        set
        {
            findOptions = value;
            foundVersion = -1;
        }
    }

    /// <summary>How many places the find query is found in.</summary>
    public int FoundCount
    {
        get
        {
            RefreshFound();
            return found.Count;
        }
    }

    /// <summary>Which of the places found is the one selected, from 0, -1 when the selection isn't one of them.</summary>
    public int FoundIndex
    {
        get
        {
            RefreshFound();
            UITextPosition start = document.SelectionStart, end = document.SelectionEnd;
            if (start.Line != end.Line)
                return -1;

            for (int i = 0; i < found.Count; i++)
            {
                if (found[i].Line == start.Line && found[i].Column == start.Column && found[i].Column + found[i].Length == end.Column)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>What's wrong with the find query as a regex, null when nothing is.</summary>
    public string? FindError => document.FindError;

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
        bool any = document.Find(findQuery, findOptions, forward);
        revealCaret = true;
        centreOnReveal = true;
        return any;
    }

    /// <summary>
    /// Selects the first place the find query is from a place on, for a find bar looking as its query is typed from where
    /// it was opened, so the match grows with the query rather than hopping along with every letter.
    /// </summary>
    public bool FindFrom(UITextPosition from)
    {
        bool any = document.FindFrom(findQuery, findOptions, from);
        revealCaret = true;
        centreOnReveal = true;
        return any;
    }

    /// <summary>Swaps every place the find query is for something else, as one step that can be undone.</summary>
    public int ReplaceAll(string with) => ReadOnly ? 0 : document.ReplaceAll(findQuery, with, findOptions);

    /// <summary>Swaps the place found that's selected for something else and selects the next, one step that can be undone.</summary>
    public bool ReplaceNext(string with)
    {
        if (ReadOnly)
            return false;

        bool replaced = document.ReplaceNext(findQuery, with, findOptions);
        revealCaret = true;
        centreOnReveal = true;
        return replaced;
    }

    // Every place it's found worked out again when the text or what's looked for changed since
    private void RefreshFound()
    {
        if (foundVersion == document.Version)
            return;

        foundVersion = document.Version;
        document.FindAll(findQuery, findOptions, found);
    }

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
        // And the room of an icon in front of them, for a line with something wrong on it
        return FontOf(skin).Measure("0", scale, markup: false).X * (Math.Max(2, digits) + GUTTER_GAP) + MathF.Round(FontOf(skin).LineHeight * scale * 0.9f);
    }

    /// <summary>How wide a piece of a line is drawn, a tab as wide as so many spaces.</summary>
    private float Width(UISkin skin, float scale, ReadOnlySpan<char> text)
    {
        float width = 0.0f;
        int tab;
        while ((tab = text.IndexOf('\t')) >= 0)
        {
            width += FontOf(skin).Measure(text[..tab], scale, markup: false).X + TabWidth(skin, scale);
            text = text[(tab + 1)..];
        }

        return width + FontOf(skin).Measure(text, scale, markup: false).X;
    }

    private float TabWidth(UISkin skin, float scale) => FontOf(skin).Measure(" ", scale, markup: false).X * TAB_SPACES;

    /// <summary>Draws a piece of a line, leaving the room of a tab where there is one.</summary>
    private float Draw(UIDrawList list, UISkin skin, float scale, ReadOnlySpan<char> text, float x, float top, Vector4 color)
    {
        int tab;
        while ((tab = text.IndexOf('\t')) >= 0)
        {
            if (tab > 0)
            {
                list.Text(text[..tab], new Vector2(x, top), scale, color, markup: false);
                x += FontOf(skin).Measure(text[..tab], scale, markup: false).X;
            }

            x += TabWidth(skin, scale);
            text = text[(tab + 1)..];
        }

        if (!text.IsEmpty)
        {
            list.Text(text, new Vector2(x, top), scale, color, markup: false);
            x += FontOf(skin).Measure(text, scale, markup: false).X;
        }

        return x;
    }

    /// <summary>The colours of every line worked out again on the editor's thread, and the lines kept to tell later which still fit them.</summary>
    private void Recolour()
    {
        coloured = changeSeen = document.Version;
        recolourNow = false;
        fresh.Clear();

        int count = document.LineCount;
        if (lineStarts.Length != count)
            lineStarts = new CodeLineStart[count];

        if (Highlight)
        {
            spans = highlighter.Read(document.Text, count, lineStarts, swatches, art);
        }
        else
        {
            spans = PlainSpans();
            swatches.Clear();
            art.Clear();
        }

        TookArt();

        if (colouredLines.Length != count)
            colouredLines = new string[count];
        for (int i = 0; i < colouredLines.Length; i++)
            colouredLines[i] = document.LineAt(i);
        FindFoldable();
    }

    // The colours worked out on a thread of the pool, from the text as it is now, taken in by Update when they're done
    private void StartColouring()
    {
        int version = document.Version, count = document.LineCount;
        string text = document.Text;
        var lines = new string[count];
        for (int i = 0; i < count; i++)
            lines[i] = document.LineAt(i);

        changeSeen = version;
        ICodeHighlighter reader = highlighter;
        colouring = Task.Run(() =>
        {
            var found = new List<HidlSwatch>();
            var named = new List<HidlArt>();
            var starts = new CodeLineStart[count];
            List<SyntaxSpan>[] read = reader.Read(text, count, starts, found, named);
            return new Colouring(version, read, lines, found, starts, named);
        });
    }

    private void TakeColouring(Colouring done)
    {
        spans = done.Spans;
        colouredLines = done.Lines;
        lineStarts = done.Starts;
        swatches.Clear();
        swatches.AddRange(done.Swatches);
        art.Clear();
        art.AddRange(done.Art);
        TookArt();
        coloured = done.Version;
        fresh.Clear();
        FindFoldable();
    }

    /// <summary>
    /// The colours of a line typed into since the last colouring, worked out for it alone and kept by its text until the
    /// next one comes back. Null for a line the last colouring never saw the start of.
    /// </summary>
    private List<SyntaxSpan>? FreshColours(int line, string text)
    {
        if (!Highlight || text.Length == 0)
            return null;

        if (fresh.TryGetValue(text, out var known))
            return known;

        // Where the line was in the last colouring, or where the lines added or taken out above it moved it from
        int was = line < colouredLines.Length ? line : line - (document.LineCount - colouredLines.Length);
        if (was < 0 || was >= lineStarts.Length)
            return null;

        if (fresh.Count >= MAX_FRESH)
            fresh.Clear();

        List<SyntaxSpan> read = highlighter.ReadLine(text, lineStarts[was]);
        fresh[text] = read;
        return read;
    }

    /// <summary>
    /// The colours of a line, null when it was typed into since they were worked out. A line is looked for where it was and
    /// where it was before the lines added or taken out above it moved it, so Enter doesn't take the colours off the rest.
    /// </summary>
    private List<SyntaxSpan>? ColoursOf(int line, string text)
    {
        if (line < colouredLines.Length && colouredLines[line] == text)
            return spans[line];

        int was = line - (document.LineCount - colouredLines.Length);
        return was >= 0 && was < colouredLines.Length && colouredLines[was] == text ? spans[was] : null;
    }

    // Every line all one span, for text that isn't coloured as code
    private List<SyntaxSpan>[] PlainSpans()
    {
        var plain = new List<SyntaxSpan>[document.LineCount];
        for (int i = 0; i < plain.Length; i++)
        {
            int length = document.LineAt(i).Length;
            plain[i] = length > 0 ? [new SyntaxSpan(0, length, SyntaxKind.Name)] : [];
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
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        EnsureRows(skin, scale);

        int index = Math.Clamp((int)MathF.Floor((area.Max.Y + scrollY - point.Y) / lineHeight), 0, Math.Max(0, rows.Count - 1));
        if (rows.Count == 0)
            return default;

        CodeRow row = rows[index];
        int line = row.Line;
        string text = document.LineAt(line);
        int rowEnd = Math.Min(row.End, text.Length);

        float wanted = point.X - (area.Min.X + TEXT_PAD - scrollX) - row.Indent;
        float before = 0.0f;

        for (int i = row.Start + 1; i <= rowEnd; i++)
        {
            float after = Width(skin, scale, text.AsSpan(row.Start, i - row.Start));
            if (wanted < (before + after) * 0.5f)
                return new UITextPosition(line, i - 1);
            before = after;
        }

        // Past the end of a row that isn't the last of its line is the start of the next, which is the same place
        return new UITextPosition(line, rowEnd);
    }

    protected override Vector2 Measure(UISkin skin) => new Vector2(480, 320) + Padding.Total;

    /* Updating */

    protected override void Update(float dt)
    {
        blink = (blink + dt) % (BLINK * 2.0f);
        tickTime = dt;
        flowTime += dt;

        // On the way to where the wheel wants it
        if (scrollGoal is { } goal)
        {
            scrollY += (goal - scrollY) * (1.0f - MathF.Exp(-SCROLL_EASE * dt));
            if (MathF.Abs(goal - scrollY) < 0.5f)
            {
                scrollY = goal;
                scrollGoal = null;
            }
        }

        if (colouring is { IsCompleted: true } finished)
        {
            colouring = null;
            if (finished.IsCompletedSuccessfully && finished.Result.Version > coloured)
                TakeColouring(finished.Result);
        }

        if (coloured != document.Version)
        {
            if (changeSeen != document.Version && colouring is null)
            {
                changeSeen = document.Version;
                sinceChange = 0.0f;
            }
            else
            {
                sinceChange += dt;
            }

            // A new text (or one that isn't coloured as code) on the spot, a change once it has been left alone a moment
            if (recolourNow || !Highlight)
                Recolour();
            else if (colouring is null && sinceChange >= RECOLOUR_DELAY)
                StartColouring();
        }

        if (checking is { IsCompleted: true } done)
        {
            checking = null;
            if (done.IsCompletedSuccessfully && done.Result.Version == document.Version)
                TakeCheck(done.Result);
        }

        if (CheckSyntax && checking is null && checkedVersion != document.Version && (checkIn -= dt) <= 0.0f)
            StartCheck();

        UpdateFolds();

        if (bracketFor != (document.Version, document.Caret))
            FindBracket();

        if (occurrencesFor != (document.Version, document.Caret))
            FindOccurrences();

        if (signatureFor != (document.Version, document.Caret))
            FindSignature();

        RefreshFound();

        if (revealCaret && paintedSkin is { } skin)
            Reveal(skin);

        UpdateTooltip();
    }

    /// <summary>Parses the code now, on the editor's thread, and underlines what the parser didn't like.</summary>
    public void Check()
    {
        checkedVersion = document.Version;
        TakeCheck(Parse(document.Version, document.Text));
    }

    // The parse a moment after typing stops, on a thread of the pool, 2.5 ms and four megabytes for two thousand lines
    // that the editor's thread doesn't wait for. What it finds is taken in by Update if the text is still what it read
    private void StartCheck()
    {
        int version = document.Version;
        string text = document.Text;
        checkedVersion = version;
        checking = Task.Run(() => Parse(version, text));
    }

    private static Checked Parse(int version, string text)
    {
        UICodeDiagnostic? error = null;
        var more = new List<UICodeDiagnostic>();

        static UICodeDiagnostic Said(ParseException e) => new(Math.Max(0, e.Line - 1), Math.Max(0, e.Col - 1), Math.Max(1, e.Length), e.Message);

        // The parser goes on after a mistake, so every one of them is underlined and not only the first
        var errors = new List<ParseException>();
        try
        {
            new Parser().ProduceSyntaxTree(Lexer.Tokenize(text), errors);
        }
        catch (ParseException e)
        {
            errors.Add(e);
        }
        catch (Exception e)
        {
            error = new UICodeDiagnostic(0, 0, 1, e.Message);
        }

        for (int i = 0; i < errors.Count; i++)
        {
            if (i == 0 && error is null) error = Said(errors[i]);
            else more.Add(Said(errors[i]));
        }

        return new Checked(version, error, more);
    }

    private void TakeCheck(Checked done)
    {
        moreErrors.Clear();
        moreErrors.AddRange(done.More);
        SyntaxError = done.Error;
        OnChecked?.Invoke(done.Error);
    }

    /// <summary>Helper method to scroll just far enough for the caret to be in sight.</summary>
    private void Reveal(UISkin skin)
    {
        revealCaret = false;

        float scale = ScaleOf(skin);
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        if (area.Height <= 0.0f)
            return;

        UITextPosition caret = document.Caret;
        EnsureRows(skin, scale);
        float top = RowOf(caret.Line, caret.Column) * lineHeight;

        // The caret has the say, wherever the wheel was taking it
        scrollGoal = null;

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
        if (wordWrap)
            scrollX = 0.0f;
        else if (x < scrollX)
            scrollX = MathF.Max(0.0f, x - width * 0.25f);
        else if (x > scrollX + width)
            scrollX = x - width * 0.75f;

        ClampScroll(skin, scale);
    }

    private void ClampScroll(UISkin skin, float scale)
    {
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        float most = MathF.Max(0.0f, Math.Max(1, rows.Count) * lineHeight - area.Height * 0.5f);
        scrollY = Math.Clamp(scrollY, 0.0f, most);
        if (scrollGoal is { } goal) scrollGoal = Math.Clamp(goal, 0.0f, most);
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
        hoveredArt = -1;
        if (!IsHovered || Module is not { } module || paintedSkin is null)
        {
            if (Tooltip.Length > 0) Tooltip = string.Empty;
            return;
        }

        // A piece of art at the end of a line is shown big, and says nothing
        Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
        for (int i = 0; i < art.Count && i < artBoxes.Length; i++)
        {
            if (artBoxes[i].Width > 0.0f && artBoxes[i].Shrink(new UIEdges(-2.0f)).Contains(pointer))
            {
                hoveredArt = i;
                if (Tooltip.Length > 0) Tooltip = string.Empty;
                return;
            }
        }

        UITextPosition at = PositionAt(pointer);
        string said = string.Empty;

        if (SyntaxError is { } error && Covers(error, at))
            said = error.Message;

        for (int i = 0; i < moreErrors.Count && said.Length == 0; i++)
        {
            if (Covers(moreErrors[i], at))
                said = moreErrors[i].Message;
        }

        // By index, a foreach over the interface is an enumerator made every tick the pointer is over the code
        for (int i = 0; i < diagnostics.Count && said.Length == 0; i++)
        {
            if (Covers(diagnostics[i], at))
                said = diagnostics[i].Message;
        }

        // Nothing wrong there, what the program has to say about the word, asked once a word
        bool hint = false;
        if (said.Length == 0 && HoverSource is { } source)
        {
            var (start, end) = document.WordAt(at);
            if (end > start && at.Column < document.LineAt(at.Line).Length)
            {
                if (hoverFor != (document.Version, at.Line, start))
                {
                    hoverFor = (document.Version, at.Line, start);
                    hoverText = source(this, new UITextPosition(at.Line, start)) ?? string.Empty;
                }

                said = hoverText;
                hint = said.Length > 0;
            }
        }

        TooltipMarkdown = hint;
        if (!ReferenceEquals(said, Tooltip))
            Tooltip = said;
    }

    // Every place the word under the caret is, whole words only, unless something is selected
    private void FindOccurrences()
    {
        occurrencesFor = (document.Version, document.Caret);
        occurrences.Clear();
        occurrenceLength = 0;

        if (document.HasSelection || !Highlight)
            return;

        UITextPosition caret = document.Caret;
        var (start, end) = document.WordAt(caret);
        if (end - start < 2)
            return;

        string line = document.LineAt(caret.Line);
        ReadOnlySpan<char> word = line.AsSpan(start, end - start);
        if (char.IsDigit(word[0]))
            return;

        for (int at = 0; at < document.LineCount && occurrences.Count < MAX_OCCURRENCES; at++)
        {
            string text = document.LineAt(at);
            for (int from = text.AsSpan().IndexOf(word, StringComparison.Ordinal); from >= 0;)
            {
                int after = from + word.Length;
                bool whole = (from == 0 || !IsWordChar(text[from - 1])) && (after >= text.Length || !IsWordChar(text[after]));
                if (whole)
                    occurrences.Add(new UITextPosition(at, from));

                int next = text.AsSpan(after).IndexOf(word, StringComparison.Ordinal);
                from = next < 0 ? -1 : after + next;
            }
        }

        // The one place it is, the one under the caret, is no help lit
        if (occurrences.Count < 2)
            occurrences.Clear();
        else
            occurrenceLength = word.Length;
    }

    private static bool IsWordChar(char character) => char.IsLetterOrDigit(character) || character == '_';

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

            // More than one caret, escape is back to one. Otherwise the focus and what is open are the compositor's
            case UIKeyboard.ESCAPE when document.HasExtraCarets:
                document.CollapseCarets();
                return;
            case UIKeyboard.ESCAPE or UIKeyboard.PASTE:
                return;

            // More carets, which can be had in a text that can only be read too, for copying
            case UIKeyboard.NEXT_OCCURRENCE:
                document.AddNextOccurrence();
                return;
            case UIKeyboard.CARET_ABOVE:
                document.AddCaretVertically(-1);
                return;
            case UIKeyboard.CARET_BELOW:
                document.AddCaretVertically(1);
                return;
            case UIKeyboard.CARETS_AT_LINE_ENDS:
                document.CaretsAtLineEnds();
                return;
            case UIKeyboard.FOLD:
                FoldAtCaret();
                return;
            case UIKeyboard.UNFOLD:
                UnfoldAtCaret();
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
                if (!document.HasSelection && !document.HasExtraCarets)
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
        CompletionTyped = typed;
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
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        UITextPosition caret = document.Caret;

        float x = area.Min.X + TEXT_PAD - scrollX + ColumnX(skin, scale, caret.Line, completionStart);
        float below = TopOf(area, caret.Line, caret.Column, lineHeight) - lineHeight;

        float widest = 0.0f;
        int last = Math.Min(completions.Count, completionTop + COMPLETION_ROWS);
        for (int i = completionTop; i < last; i++)
        {
            var item = completions[i];
            float text = FontOf(skin).Measure(item.Text, scale, markup: false).X;
            float detail = item.Detail.Length > 0 ? FontOf(skin).Measure(item.Detail, scale * 0.85f, markup: false).X + lineHeight : 0.0f;
            widest = MathF.Max(widest, text + detail);
        }

        float width = widest + TEXT_PAD * 4.0f;
        float height = Math.Min(COMPLETION_ROWS, completions.Count) * lineHeight + TEXT_PAD;

        // Under the line unless that runs off the bottom of the module
        float top = below;
        if (Module is { } module && top - height < module.Frame.Min.Y)
            top = TopOf(area, caret.Line, caret.Column, lineHeight) + height;

        return new UIRect(new Vector2(x - TEXT_PAD, top - height), new Vector2(x - TEXT_PAD + width, top));
    }

    protected internal override bool PopupContains(Vector2 point) => CompletionBounds().Contains(point);

    protected internal override void PaintPopup(UIDrawList list)
    {
        list.PushFont(FontOf(list.Skin));
        PaintCompletions(list);
        list.PopFont();
    }

    // When the list of completions was first painted, by the draw list's clock, less than 0 while there's none
    private float completionsOpenedAt = -1.0f;

    private void PaintCompletions(UIDrawList list)
    {
        UIRect bounds = CompletionBounds();
        if (bounds.IsEmpty || paintedSkin is not { } skin)
        {
            completionsOpenedAt = -1.0f;
            return;
        }

        float scale = paintedScale;
        float lineHeight = FontOf(skin).LineHeight * scale;
        UISyntaxColors colors = skin.Syntax;

        // On what every popup sits on, coming in out of the line it's for
        bool entering = list.BeginEntrance(ref completionsOpenedAt, new Vector2(bounds.Min.X, bounds.Max.Y), new Vector2(0.0f, 4.0f));
        list.Popup(bounds, Module?.Compositor.PopupGlass);

        int last = Math.Min(completions.Count, completionTop + COMPLETION_ROWS);
        for (int i = completionTop; i < last; i++)
        {
            list.BeginPart(i);
            float top = bounds.Max.Y - TEXT_PAD * 0.5f - (i - completionTop) * lineHeight;
            var row = new UIRect(new Vector2(bounds.Min.X + 1.0f, top - lineHeight), new Vector2(bounds.Max.X - 1.0f, top));

            if (i == completionIndex)
                list.RoundRect(row.Shrink(new UIEdges(3.0f, 1.0f)), skin.CornerRadius, skin.AccentColor with { W = 0.35f });

            var item = completions[i];
            list.Text(item.Text, new Vector2(bounds.Min.X + TEXT_PAD, top), scale, colors.Name, markup: false);

            if (item.Detail.Length > 0)
            {
                float detail = FontOf(skin).Measure(item.Detail, scale * 0.85f, markup: false).X;
                list.Text(item.Detail, new Vector2(bounds.Max.X - TEXT_PAD - detail, top - lineHeight * 0.08f), scale * 0.85f, colors.Comment, markup: false);
            }
        }

        list.EndParts();

        if (entering)
            list.EndEntrance();
    }

    protected internal override void OnPaste(string text)
    {
        if (!ReadOnly)
            document.Insert(text);
    }

    // What control with C takes, the selection (every caret's, a line each), or the caret's whole line without one, the
    // way code editors do it
    private string CopiedText() => document.HasExtraCarets ? document.SelectedTextOfEvery()
        : document.HasSelection ? document.SelectedText : document.LineAt(document.Caret.Line) + "\n";

    private void SelectLine(int line) =>
        document.Select(new UITextPosition(line, 0), line + 1 < document.LineCount ? new UITextPosition(line + 1, 0) : new UITextPosition(line, document.LineAt(line).Length));

    private int PageLines()
    {
        if (paintedSkin is not { } skin)
            return 20;

        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        return Math.Max(1, (int)(TextArea(skin, paintedScale).Height / lineHeight) - 1);
    }

    /* The pointer */

    private bool draggingBar;

    // The word a double click picked, while the press goes on dragging whole words from it, null otherwise
    private (UITextPosition From, UITextPosition To)? wordDrag;

    // Where on the thumb it was taken hold of, from its top, so it doesn't jump to put its middle under the pointer
    private float barGrab;

    /// <summary>
    /// Helper method for the bar, the track it runs in, how long its thumb is, how far down it can go and where its top is
    /// now. The paint and the drag go by the same numbers, they went by two and the thumb crept away from the pointer.
    /// </summary>
    private (UIRect Track, float Length, float Most, float Top) BarGeometry(UISkin skin, float scale)
    {
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        UIRect track = new(new Vector2(area.Max.X, area.Min.Y), new Vector2(area.Max.X + BAR_WIDTH, area.Max.Y));
        float total = Math.Max(1, rows.Count) * lineHeight;
        float length = MathF.Max(24.0f, track.Height * area.Height / MathF.Max(1.0f, total));
        float most = MathF.Max(1.0f, total - area.Height * 0.5f);
        float top = track.Max.Y - (track.Height - length) * Math.Clamp(scrollY / most, 0.0f, 1.0f);
        return (track, MathF.Min(length, track.Height), most, top);
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        blink = 0.0f;

        // A swatch of a colour written in the code, to pick another
        if (OnSwatchPressed is { } swatched && paintedSkin is { } swatchSkin)
        {
            float lineHeight = FontOf(swatchSkin).LineHeight * paintedScale;
            UIRect area = TextArea(swatchSkin, paintedScale);
            float left = area.Min.X + TEXT_PAD - scrollX;
            for (int i = 0; i < swatches.Count; i++)
            {
                if (SwatchBounds(swatchSkin, paintedScale, area, left, lineHeight, i).Shrink(new UIEdges(-2.0f)).Contains(point))
                {
                    swatched(swatches[i]);
                    return;
                }
            }
        }

        // A press on the list of what can be completed picks the row it is on
        if (CompletionOpen && CompletionBounds() is var listed && listed.Contains(point) && paintedSkin is { } listSkin)
        {
            float rowHeight = FontOf(listSkin).LineHeight * paintedScale;
            int row = completionTop + (int)((listed.Max.Y - TEXT_PAD * 0.5f - point.Y) / rowHeight);
            if (row >= 0 && row < completions.Count)
            {
                completionIndex = row;
                AcceptCompletion();
            }
            return;
        }

        CloseCompletion();
        wordDrag = null;

        if (paintedSkin is { } skin && point.X >= TextArea(skin, paintedScale).Max.X)
        {
            // Taken by the thumb it stays where it was taken, anywhere else on the track the thumb's middle goes there
            var (_, length, _, top) = BarGeometry(skin, paintedScale);
            barGrab = point.Y <= top && point.Y >= top - length ? top - point.Y : length * 0.5f;
            draggingBar = true;
            OnPointerDrag(point);
            return;
        }

        // The lines held at the top go to where they are
        if (PressSticky(point))
            return;

        UITextPosition at = PositionAt(point);
        double now = Environment.TickCount64 / 1000.0;

        // In the gutter a press on a chevron folds, anywhere else in it is the whole line
        if (PressFold(point, at))
            return;

        if (paintedSkin is { } painted && point.X < TextArea(painted, paintedScale).Min.X)
        {
            // The strip the marks go in, as wide as one of them, is the breakpoints' when somebody wants it
            float strip = Bounds.Shrink(Padding).Min.X + 2.0f + MathF.Round(FontOf(painted).LineHeight * paintedScale * 0.8f) + 2.0f;
            if (OnGutterPressed is not null && point.X < strip)
            {
                OnGutterPressed(at.Line);
                return;
            }

            SelectLine(at.Line);
            return;
        }

        if (now - lastPress < DOUBLE_CLICK && at == lastPressAt)
        {
            document.SelectWordAt(at);
            wordDrag = (document.Anchor, document.Caret);
            lastPress = -1.0;
            return;
        }

        lastPress = now;
        lastPressAt = at;

        // Alt puts down another caret, alt and shift dragged makes a column of them
        var keyboard = GameEngine.Instance.Input.Keyboard;
        if (keyboard.Alt && keyboard.Shift)
        {
            columnFrom = at;
            document.SelectColumn(at, at);
            return;
        }

        if (keyboard.Alt)
        {
            document.AddCaret(at);
            return;
        }

        document.MoveTo(at, keyboard.Shift);
    }

    // Where a column being dragged out with alt and shift started, null otherwise
    private UITextPosition? columnFrom;

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (draggingBar)
        {
            if (paintedSkin is { } skin)
            {
                var (track, length, most, _) = BarGeometry(skin, paintedScale);
                float top = point.Y + barGrab;
                float share = Math.Clamp((track.Max.Y - top) / MathF.Max(1.0f, track.Height - length), 0.0f, 1.0f);
                scrollGoal = null;
                scrollY = share * most;
            }

            return;
        }

        if (columnFrom is { } column)
            document.SelectColumn(column, PositionAt(point));
        else if (wordDrag is { } word)
            document.SelectWordsTo(word.From, word.To, PositionAt(point));
        else if (!document.HasExtraCarets)
            document.MoveTo(PositionAt(point), selecting: true);
        revealCaret = true;
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        draggingBar = false;
        wordDrag = null;
        columnFrom = null;
    }

    protected internal override bool OnScroll(float delta)
    {
        if (OnZoom is { } zoom && GameEngine.Instance?.Input.Keyboard.Control == true)
        {
            zoom(delta);
            return true;
        }

        if (paintedSkin is not { } skin)
            return false;

        // Where it's going, from where it was going if it hadn't got there yet, so a quick turn of the wheel adds up
        scrollGoal = (scrollGoal ?? scrollY) - delta * WHEEL_LINES * FontOf(skin).LineHeight * paintedScale;
        ClampScroll(skin, paintedScale);
        return true;
    }

    /* Painting */

    protected override void Paint(UIDrawList list)
    {
        list.PushFont(FontOf(list.Skin));
        PaintCode(list);
        list.PopFont();
    }

    private void PaintCode(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UISyntaxColors colors = skin.Syntax;
        paintedSkin = skin;

        float scale = ScaleOf(skin);
        paintedScale = scale;
        EnsureRows(skin, scale);

        float lineHeight = FontOf(skin).LineHeight * scale;
        float digit = FontOf(skin).Measure("0", scale, markup: false).X;
        UIRect content = Bounds.Shrink(Padding);
        UIRect area = TextArea(skin, scale);
        float left = area.Min.X + TEXT_PAD - scrollX;

        list.BeginPart(PART_BACK);
        list.Box(Bounds, Color ?? (skin.FieldColor.W > 0.0f ? skin.FieldColor : new Vector4(0.0f, 0.0f, 0.0f, 0.35f)));

        // The picture behind it all, in the corner the code is least often written in
        if (Backdrop is { IsValid: true } picture && BackdropOpacity > 0.0f)
        {
            list.BeginPart(PART_BACKDROP);
            list.PushClip(area);
            list.Image(picture, BackdropBounds(area, picture), new Vector4(1.0f, 1.0f, 1.0f, BackdropOpacity));
            list.PopClip();
        }

        // The rows in sight, and the lines they are the rows of
        int first = Math.Clamp((int)(scrollY / lineHeight), 0, Math.Max(0, rows.Count - 1));
        int last = Math.Min(rows.Count - 1, first + (int)(area.Height / lineHeight) + 1);
        int firstLine = rows.Count > 0 ? rows[first].Line : 0, lastLine = rows.Count > 0 ? rows[last].Line : 0;

        bool focused = IsFocused;
        UITextPosition start = document.SelectionStart, end = document.SelectionEnd;
        Vector4 selection = skin.AccentColor with { W = 0.35f };
        Vector4 band = skin.HoverColor with { W = skin.HoverColor.W * 0.35f };

        list.PushClip(area);

        // What is behind the code, row by row. The caret's line, the selections, what is found, the brackets
        list.BeginPart(PART_OVERLAY);
        var extras = document.ExtraCarets;
        for (int index = first; index <= last; index++)
        {
            CodeRow row = rows[index];
            float top = RowTop(area, index, lineHeight);

            if (focused && !document.HasSelection && !document.HasExtraCarets && row.Line == document.Caret.Line)
                list.Rect(new UIRect(new Vector2(area.Min.X, top - lineHeight), new Vector2(area.Max.X, top)), band);

            if (row.Line == ExecutionLine)
                list.Rect(new UIRect(new Vector2(area.Min.X, top - lineHeight), new Vector2(area.Max.X, top)), skin.WarningColor with { W = 0.2f });

            if (document.HasSelection)
                PaintSelected(list, skin, scale, left, top, lineHeight, digit, row, start, end, selection);

            // By index, a foreach over the interface would make an enumerator every frame
            for (int i = 0; i < extras.Count; i++)
            {
                if (!extras[i].IsEmpty)
                    PaintSelected(list, skin, scale, left, top, lineHeight, digit, row, extras[i].Start, extras[i].End, selection);
            }
        }

        if (findQuery.Length > 0)
        {
            // The one selected stronger, it's the one enter goes on from
            Vector4 lit = skin.HighlightColor with { W = 0.18f };
            Vector4 current = skin.HighlightColor with { W = 0.45f };
            for (int i = 0; i < found.Count; i++)
            {
                UITextMatch at = found[i];
                if (at.Line < firstLine || at.Line > lastLine) continue;

                bool selected = start.Line == at.Line && start.Column == at.Column && end.Line == at.Line && end.Column == at.Column + at.Length;
                UIRect box = Piece(skin, scale, area, left, lineHeight, at.Line, at.Column, at.Column + at.Length);
                list.Rect(box, selected ? current : lit);
                if (selected)
                    list.Outline(box, 1.0f, skin.HighlightColor with { W = 0.8f });
            }
        }

        if (occurrenceLength > 0)
        {
            Vector4 same = skin.TextColor with { W = 0.1f };
            for (int i = 0; i < occurrences.Count; i++)
            {
                UITextPosition at = occurrences[i];
                if (at.Line >= firstLine && at.Line <= lastLine)
                    list.Rect(Piece(skin, scale, area, left, lineHeight, at.Line, at.Column, at.Column + occurrenceLength), same);
            }
        }

        if (IndentGuides)
            PaintGuides(list, skin, scale, area, left, lineHeight, first, last);

        if (focused && bracket is { } one && bracketPair is { } two)
        {
            PaintBracket(list, skin, scale, area, left, lineHeight, one, firstLine, lastLine);
            PaintBracket(list, skin, scale, area, left, lineHeight, two, firstLine, lastLine);
        }

        // The code itself, a row at a time, each a part of its own so a row scrolled along is blended with itself. By what
        // it is (its line and where in it it starts), not by its number, which is somebody else's row once the code is
        // wrapped again (round the picture as it scrolls by, a word typed further up), and every letter of it blended
        // with the same letter of that row wherever it was, the text a jumble of flying letters for a frame
        Span<char> number = stackalloc char[12];
        float origin = area.Max.Y + scrollY;
        BeginFlow(lineHeight);
        for (int index = first; index <= last; index++)
        {
            CodeRow row = rows[index];
            list.BeginPart(HashCode.Combine(row.Line, row.Start) & 0x3FFFFFFF);
            float top = RowTop(area, index, lineHeight);
            string written = document.LineAt(row.Line);
            float x = left + row.Indent;
            int rowEnd = Math.Min(row.End, written.Length);

            // Typed into and not coloured with the rest yet, coloured on its own, or all one colour for a moment when it
            // can't be (it starts inside a comment over several lines)
            if ((ColoursOf(row.Line, written) ?? FreshColours(row.Line, written)) is not { } colours)
            {
                x = DrawFlowing(list, skin, scale, row.Line, row.Start, written.AsSpan(row.Start, rowEnd - row.Start), x, top, colors.Of(SyntaxKind.Name), left, origin);
            }
            else
            {
                int at = row.Start;
                foreach (SyntaxSpan span in colours)
                {
                    if (span.Start + span.Length > written.Length)
                        break;

                    // Only what of the span is on this row
                    int from = Math.Max(span.Start, row.Start), to = Math.Min(span.Start + span.Length, rowEnd);
                    if (to <= from)
                        continue;

                    x += Width(skin, scale, written.AsSpan(at, from - at));
                    x = DrawFlowing(list, skin, scale, row.Line, from, written.AsSpan(from, to - from), x, top, colors.Of(span.Kind), left, origin);
                    at = to;
                }

                if (at < rowEnd)
                    x = DrawFlowing(list, skin, scale, row.Line, at, written.AsSpan(at, rowEnd - at), x, top, colors.Of(SyntaxKind.Name), left, origin);
            }

            // A folded line says what it folds away after it, the closing line, the way VS Code does
            if (rowEnd == written.Length && folded.Contains(row.Line) && foldable.TryGetValue(row.Line, out int foldEnd))
                PaintFolded(list, skin, scale, x, top, lineHeight, foldEnd, colors);
        }

        // The colours written in the code, a swatch at the end of their line
        list.BeginPart(PART_SWATCHES);
        for (int i = 0; i < swatches.Count; i++)
        {
            HidlSwatch swatch = swatches[i];
            if (swatch.Line < firstLine || swatch.Line > lastLine || swatch.Line >= document.LineCount || IsHidden(swatch.Line, out _)) continue;

            UIRect box = SwatchBounds(skin, scale, area, left, lineHeight, i);
            list.Rect(box.Shrink(new UIEdges(-1.0f)), skin.TextColor with { W = 0.5f });
            list.Rect(box, swatch.Color with { W = 1.0f });
        }

        // The art named in the code, after the swatches
        PaintArt(list, skin, scale, area, left, lineHeight, firstLine, lastLine);

        // What the program has to say after the end of a line, the value a let came to
        for (int i = 0; i < inlineHints.Count; i++)
        {
            UICodeHint hint = inlineHints[i];
            if (hint.Line < firstLine || hint.Line > lastLine || hint.Line >= document.LineCount || IsHidden(hint.Line, out _))
                continue;

            int length = document.LineAt(hint.Line).Length;
            float x = left + ColumnX(skin, scale, hint.Line, length) + digit * (SwatchesOn(hint.Line) * 2 + 2) + ArtWidthOn(hint.Line);
            list.Text(hint.Text, new Vector2(x, TopOf(area, hint.Line, length, lineHeight)), scale, colors.Comment with { W = colors.Comment.W * 0.8f }, markup: false);
        }

        // What is wrong, a line under it
        list.BeginPart(PART_CARET);
        if (SyntaxError is { } error)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, error, firstLine, lastLine);
        for (int i = 0; i < moreErrors.Count; i++)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, moreErrors[i], firstLine, lastLine);
        for (int i = 0; i < diagnostics.Count; i++)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, diagnostics[i], firstLine, lastLine);

        // How the call being typed is called, over it
        if (focused && signature is { } said)
            PaintSignature(list, skin, scale, area, left, lineHeight, first, said);

        // The caret, which blinks, and stays lit while it is moved or typed with, and every other caret there is
        if (focused && blink < BLINK)
        {
            PaintCaret(list, skin, scale, area, left, lineHeight, first, last, document.Caret);
            for (int i = 0; i < extras.Count; i++)
                PaintCaret(list, skin, scale, area, left, lineHeight, first, last, extras[i].Caret);
        }

        list.PopClip();

        // The numbers down the side, the caret's lit, a mark where something is wrong, and a chevron for what folds
        if (LineNumbers)
        {
            list.PushClip(new UIRect(content.Min, new Vector2(area.Min.X, content.Max.Y)));
            Vector2 pointerAt = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : Vector2.Zero;
            bool overGutter = IsHovered && pointerAt.X < area.Min.X;
            float pointerY = pointerAt.Y;
            for (int index = first; index <= last; index++)
            {
                CodeRow row = rows[index];
                if (row.Start > 0)
                    continue;

                int line = row.Line;
                list.BeginPart(PART_GUTTER + line);
                float top = FlowTop(line, RowTop(area, index, lineHeight), origin);
                if (!(line + 1).TryFormat(number, out int length))
                    continue;

                float right = area.Min.X - digit * GUTTER_GAP;
                Vector4 shade = line == document.Caret.Line ? colors.Name : colors.LineNumber;

                // What is wrong has its icon at the far side of the gutter, the number lit the same colour. A breakpoint
                // has that place before it, and the line a debugger stopped on its arrow over that
                bool breakpoint = Breakpoints?.Contains(line) == true;
                bool marked = IsMarked(line, out bool warning);
                if (marked)
                    shade = warning ? skin.WarningColor : skin.ErrorColor;

                float markSide = MathF.Round(lineHeight * 0.8f);
                float markMiddle = top - lineHeight * 0.5f;
                var slot = new UIRect(new Vector2(content.Min.X + 2.0f, markMiddle - markSide * 0.5f), new Vector2(content.Min.X + 2.0f + markSide, markMiddle + markSide * 0.5f));
                bool pointing = OnGutterPressed is not null && overGutter && pointerY <= top && pointerY > top - lineHeight;
                if (breakpoint || pointing)
                {
                    float dot = MathF.Round(markSide * 0.32f);
                    var round = new UIRect(slot.Center - new Vector2(dot), slot.Center + new Vector2(dot));
                    list.RoundRect(round, dot, skin.ErrorColor with { W = breakpoint ? 1.0f : 0.35f });
                }
                else if (marked)
                {
                    list.Symbol(warning ? "warning" : "error", slot, shade);
                }

                if (line == ExecutionLine)
                    list.Symbol("debug-stackframe", slot, skin.WarningColor);

                list.Text(number[..length], new Vector2(right - digit * length, top), scale, shade, markup: false);

                // What can fold has its chevron in the gap after the number, always when it's folded, under the pointer otherwise
                bool isFolded = folded.Contains(line);
                if ((isFolded || overGutter) && foldable.ContainsKey(line))
                {
                    float side = MathF.Round(lineHeight * 0.7f);
                    float middle = top - lineHeight * 0.5f;
                    float from = area.Min.X - digit * GUTTER_GAP + (digit * GUTTER_GAP - side) * 0.5f;
                    list.Symbol(isFolded ? "chevron-right" : "chevron-down", new UIRect(new Vector2(from, middle - side * 0.5f), new Vector2(from + side, middle + side * 0.5f)),
                        isFolded ? colors.Name : colors.LineNumber);
                }
            }
            list.PopClip();
        }

        EndFlow();

        // The lines the top of the view is inside of, held there over the code and its numbers, a click on one going to it
        list.PushClip(new UIRect(content.Min, new Vector2(area.Max.X, content.Max.Y)));
        PaintSticky(list, skin, scale, content, area, lineHeight, digit, firstLine, colors);
        list.PopClip();

        // The bar, as long against its track as what is in sight is against all of it, and the line along the top that
        // says it has the keyboard
        list.BeginPart(PART_BAR);
        if (FocusLine && focused)
            list.Rect(new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - 2.0f), Bounds.Max), skin.AccentColor);
        float total = rows.Count * lineHeight;
        if (total > area.Height)
        {
            var (track, length, _, topOfThumb) = BarGeometry(skin, paintedScale);

            list.Rect(track, skin.HoverColor with { W = skin.HoverColor.W * 0.5f });
            list.Rect(new UIRect(new Vector2(track.Min.X, topOfThumb - length), new Vector2(track.Max.X, topOfThumb)), skin.AccentColor with { W = 0.8f });
        }

        // The art under the pointer, big, over everything
        if (hoveredArt >= 0 && hoveredArt < art.Count && hoveredArt < artBoxes.Length && artBoxes[hoveredArt].Width > 0.0f)
            PaintArtLarge(list, skin, lineHeight, art[hoveredArt], artBoxes[hoveredArt]);

        list.EndParts();
    }

    // The box a piece of a line takes from one column to another, on the row the first is on
    private UIRect Piece(UISkin skin, float scale, UIRect area, float left, float lineHeight, int line, int from, int to)
    {
        float top = TopOf(area, line, from, lineHeight);
        float x0 = ColumnX(skin, scale, line, from);
        float x1 = RowOf(line, to) == RowOf(line, from) ? ColumnX(skin, scale, line, to) : x0 + (to - from) * FontOf(skin).Measure("0", scale, markup: false).X;
        return new UIRect(new Vector2(left + x0, top - lineHeight), new Vector2(left + x1, top));
    }

    /* Signature help, how the function being called at the caret is called */

    private UISignature? signature;
    private UITextPosition signatureAt;
    private (int Version, UITextPosition Caret) signatureFor = (-1, default);

    // Back from the caret to the bracket of the call it's inside of, counting the commas on the way, and the name before it
    private void FindSignature()
    {
        signatureFor = (document.Version, document.Caret);
        signature = null;
        if (SignatureSource is not { } source || document.HasSelection)
            return;

        int line = document.Caret.Line, column = document.Caret.Column, depth = 0, commas = 0;
        for (int steps = 0; steps < 4000; steps++)
        {
            if (column == 0)
            {
                if (--line < 0)
                    return;
                column = document.LineAt(line).Length;
                continue;
            }

            string text = document.LineAt(line);
            char at = text[--column];
            if (at == '"')
            {
                // A text is gone over whole, its commas and brackets aren't the call's
                int open = column > 0 ? text.LastIndexOf('"', column - 1) : -1;
                if (open < 0)
                    return;
                column = open;
            }
            else if (at is ')' or ']' or '}')
            {
                depth++;
            }
            else if (at is '(' or '[' or '{')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                // Inside a list or an object rather than a call, or a bracket with no name before it, nothing to say
                if (at != '(')
                    return;

                int end = column;
                while (end > 0 && text[end - 1] == ' ') end--;
                int start = end;
                while (start > 0 && (UITextDocument.IsWordChar(text[start - 1]) || text[start - 1] == '.')) start--;
                if (end <= start)
                    return;

                signature = source(text[start..end], commas);
                signatureAt = new UITextPosition(line, start);
                return;
            }
            else if (at == ',' && depth == 0)
            {
                commas++;
            }
            else if (at == ';' && depth == 0)
            {
                return;
            }
        }
    }

    // The signature in a box over the row of the call's name (under it when that's the top row), the argument the caret is
    // on in bold
    private void PaintSignature(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, UISignature said)
    {
        if (said.Text.Length == 0)
            return;

        list.BeginPart(PART_SIGNATURE);
        float small = scale * 0.92f;
        float height = FontOf(skin).LineHeight * small + TEXT_PAD;
        float width = FontOf(skin).Measure(said.Text, small, markup: false).X + TEXT_PAD * 2.0f + FontOf(skin).Measure(" ", small, markup: false).X;
        int row = RowOf(signatureAt.Line, signatureAt.Column);
        float top = RowTop(area, row, lineHeight);
        float x = MathF.Max(area.Min.X, left + ColumnX(skin, scale, signatureAt.Line, signatureAt.Column) - TEXT_PAD);
        float bottom = row > first ? top : top - lineHeight - height;
        if (bottom + height > area.Max.Y) bottom = top - lineHeight - height;

        var box = new UIRect(new Vector2(x, bottom), new Vector2(x + width, bottom + height));
        list.Popup(box, Module?.Compositor.PopupGlass);

        int start = Math.Clamp(said.Start, 0, said.Text.Length), length = Math.Clamp(said.Length, 0, said.Text.Length - start);
        Vector4 text = skin.TextColor with { W = skin.TextColor.W * 0.75f };
        float pen = box.Min.X + TEXT_PAD, textTop = box.Max.Y - TEXT_PAD * 0.5f;
        ReadOnlySpan<char> all = said.Text;
        list.Text(all[..start], new Vector2(pen, textTop), small, text, markup: false);
        pen += FontOf(skin).Measure(all[..start], small, markup: false).X;
        list.Text(all.Slice(start, length), new Vector2(pen, textTop), small, skin.HighlightColor, false, UITextStyle.Bold, default);
        pen += FontOf(skin).Measure(all.Slice(start, length), small, markup: false).X + FontOf(skin).BoldAdvance * small * length;
        list.Text(all[(start + length)..], new Vector2(pen, textTop), small, text, markup: false);
    }

    // The part of a selection on a row, to the end of the row and a little past it on the rows it runs over
    private void PaintSelected(UIDrawList list, UISkin skin, float scale, float left, float top, float lineHeight, float digit, CodeRow row, UITextPosition start, UITextPosition end, Vector4 color)
    {
        var rowStart = new UITextPosition(row.Line, row.Start);
        var rowEnd = new UITextPosition(row.Line, row.End);
        if (end < rowStart || start > rowEnd || (end == rowStart && row.Start > 0))
            return;

        string text = document.LineAt(row.Line);
        int fromColumn = start > rowStart ? start.Column : row.Start;
        int toColumn = end < rowEnd ? end.Column : row.End;

        float from = row.Indent + Width(skin, scale, text.AsSpan(row.Start, Math.Max(0, fromColumn - row.Start)));
        float to = row.Indent + Width(skin, scale, text.AsSpan(row.Start, Math.Max(0, toColumn - row.Start)));
        if (end > rowEnd && row.End == text.Length)
            to += digit * 0.5f;

        if (to > from)
            list.Rect(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top)), color);
    }

    /// <summary>
    /// How long the caret takes to glide to where it went, in seconds (about two thirds of the way in that long), 0 for
    /// it to jump there the way it always did. Only the caret that is drawn glides, typing goes where the caret is.
    /// </summary>
    public float CaretGlide { get; set; }

    /// <summary>Where the caret was drawn last, the middle of its left edge, for something to happen there (sparks).</summary>
    public Vector2 CaretPoint { get; private set; }

    // How long the last tick was, for the glide, and where the caret is drawn against the code (from the top of its first
    // row, so scrolling moves it with the code rather than it gliding after it)
    private float tickTime;
    private Vector2 caretDrawn;
    private bool caretSeen;

    private void PaintCaret(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, int last, UITextPosition caret)
    {
        int row = RowOf(caret.Line, caret.Column);
        if (row < first || row > last || IsHidden(caret.Line, out _))
            return;

        float top = RowTop(area, row, lineHeight);
        float x = left + ColumnX(skin, scale, caret.Line, caret.Column);
        if (caret == document.Caret)
        {
            Vector2 origin = new(left, RowTop(area, 0, lineHeight));
            Vector2 wanted = new Vector2(x, top) - origin;

            // A long way (a page, the end of the file) is jumped, a glide over half the screen only looks slow
            if (CaretGlide <= 0.0f || !caretSeen || Vector2.Distance(caretDrawn, wanted) > lineHeight * 12.0f)
                caretDrawn = wanted;
            else
            {
                caretDrawn += (wanted - caretDrawn) * (1.0f - MathF.Exp(-tickTime / CaretGlide));
                if (Vector2.DistanceSquared(caretDrawn, wanted) < 0.25f)
                    caretDrawn = wanted;
            }

            caretSeen = true;
            x = origin.X + caretDrawn.X;
            top = origin.Y + caretDrawn.Y;
            CaretPoint = new Vector2(x, top - lineHeight * 0.5f);
        }

        list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + MathF.Max(1.0f, MathF.Round(scale * 8.0f)), top)), skin.HighlightColor);
    }

    // After a folded line, a box with the lines it hides counted and the line that closes it, "… }"
    private void PaintFolded(UIDrawList list, UISkin skin, float scale, float x, float top, float lineHeight, int foldEnd, UISyntaxColors colors)
    {
        string closing = foldEnd < document.LineCount ? document.LineAt(foldEnd).Trim() : string.Empty;
        float gap = FontOf(skin).Measure(" ", scale, markup: false).X;
        float dots = FontOf(skin).Measure("…", scale, markup: false).X;
        var box = new UIRect(new Vector2(x + gap, top - lineHeight + 2.0f), new Vector2(x + gap * 3.0f + dots, top - 2.0f));
        list.Rect(box, skin.HoverColor);
        list.Text("…", new Vector2(x + gap * 2.0f, top), scale, colors.Comment, markup: false);
        if (closing.Length > 0)
            Draw(list, skin, scale, closing, box.Max.X + gap, top, colors.Of(SyntaxKind.Punctuation));
    }

    // The lines the top of the view is inside of, on a strip of their own over the code and the gutter, numbered
    private void PaintSticky(UIDrawList list, UISkin skin, float scale, UIRect content, UIRect area, float lineHeight, float digit, int firstLine, UISyntaxColors colors)
    {
        List<int> held = StickyLines(firstLine);
        if (held.Count == 0)
            return;

        list.BeginPart(PART_STICKY);
        UIRect strip = StickyBounds(area, lineHeight, held.Count);
        strip = new UIRect(new Vector2(content.Min.X, strip.Min.Y), strip.Max);
        list.Rect(strip, Color ?? (skin.PanelColor with { W = 1.0f }));
        list.Rect(new UIRect(strip.Min, new Vector2(strip.Max.X, strip.Min.Y + 1.0f)), skin.TextColor with { W = 0.15f });

        Span<char> number = stackalloc char[12];
        for (int i = 0; i < held.Count; i++)
        {
            int line = held[i];
            string written = document.LineAt(line);
            float top = area.Max.Y - i * lineHeight;
            float x = area.Min.X + TEXT_PAD;
            int at = 0;

            if (LineNumbers && (line + 1).TryFormat(number, out int length))
                list.Text(number[..length], new Vector2(area.Min.X - digit * GUTTER_GAP - digit * length, top), scale, colors.LineNumber, markup: false);

            if ((ColoursOf(line, written) ?? FreshColours(line, written)) is { } colours)
            {
                foreach (SyntaxSpan span in colours)
                {
                    if (span.Start + span.Length > written.Length)
                        break;
                    x += Width(skin, scale, written.AsSpan(at, span.Start - at));
                    x = Draw(list, skin, scale, written.AsSpan(span.Start, span.Length), x, top, colors.Of(span.Kind));
                    at = span.Start + span.Length;
                }
            }
            else
            {
                Draw(list, skin, scale, written, x, top, colors.Of(SyntaxKind.Name));
            }
        }
    }

    // How many swatches a line has at its end, for what's written after them
    private int SwatchesOn(int line)
    {
        int count = 0;
        for (int i = 0; i < swatches.Count; i++)
        {
            if (swatches[i].Line == line)
                count++;
        }
        return count;
    }

    // Where the swatch of a colour written in the code is, after the end of its line and after the swatches before it there
    private UIRect SwatchBounds(UISkin skin, float scale, UIRect area, float left, float lineHeight, int index)
    {
        HidlSwatch swatch = swatches[index];
        int before = 0;
        for (int i = index - 1; i >= 0 && swatches[i].Line == swatch.Line; i--)
            before++;

        int length = document.LineAt(swatch.Line).Length;
        float side = MathF.Round(lineHeight * 0.6f);
        float top = TopOf(area, swatch.Line, length, lineHeight);
        float x = left + ColumnX(skin, scale, swatch.Line, length) + side * 0.8f + before * (side + 4.0f);
        float middle = MathF.Round(top - lineHeight * 0.5f);
        return new UIRect(new Vector2(x, middle - side * 0.5f), new Vector2(x + side, middle + side * 0.5f));
    }

    // After new art came with the colours, room for where it's drawn
    private void TookArt()
    {
        if (artBoxes.Length < art.Count)
            artBoxes = new UIRect[Math.Max(art.Count, artBoxes.Length * 2)];

        Array.Clear(artBoxes);
        hoveredArt = -1;
    }

    // How big a piece of art is drawn at the end of its line, and how far from the next
    private static float ArtSide(float lineHeight) => MathF.Round(lineHeight * 0.95f);

    // A picture wider than it is tall takes as much more room along the line, up to this many times as wide as tall
    private const float ART_WIDEST = 2.5f;

    private const float ART_GAP = 4.0f;

    // How many pixels a side a picture is put into the skin's atlas at the most, shown big it's still sharp enough, and a
    // whole screen's loading picture would never fit in there as it is
    private const int ART_MOST = 256;

    // The art at the end of the lines in sight, one after the other after the swatches, what there is no picture of left out
    private void PaintArt(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int firstLine, int lastLine)
    {
        Array.Clear(artBoxes);
        if (!ShowArt || art.Count == 0)
            return;

        list.BeginPart(PART_ART);
        float side = ArtSide(lineHeight), swatchSide = MathF.Round(lineHeight * 0.6f);
        int line = -1;
        float x = 0.0f, middle = 0.0f;

        for (int i = 0; i < art.Count && i < artBoxes.Length; i++)
        {
            HidlArt item = art[i];
            if (item.Line < firstLine || item.Line > lastLine || item.Line >= document.LineCount || IsHidden(item.Line, out _))
                continue;

            if (item.Line != line)
            {
                line = item.Line;
                int length = document.LineAt(line).Length;
                x = left + ColumnX(skin, scale, line, length) + swatchSide * 0.8f + SwatchesOn(line) * (swatchSide + ART_GAP);
                middle = MathF.Round(TopOf(area, line, length, lineHeight) - lineHeight * 0.5f);
            }

            Vector2 texels = TexelsOf(skin, item);
            float wide = texels.Y > 0.0f ? Math.Clamp(texels.X / texels.Y, 1.0f, ART_WIDEST) : 1.0f;
            var box = new UIRect(new Vector2(x, middle - side * 0.5f), new Vector2(x + MathF.Round(side * wide), middle + side * 0.5f));
            if (DrawArt(list, skin, item, box, skin.TextColor))
            {
                artBoxes[i] = box;
                x = box.Max.X + ART_GAP;
            }
        }
    }

    // How much room the art at the end of a line took the last time it was drawn, for what's written after it
    private float ArtWidthOn(int line)
    {
        float width = 0.0f;
        for (int i = 0; i < art.Count && i < artBoxes.Length; i++)
        {
            if (art[i].Line == line && artBoxes[i].Width > 0.0f)
                width += artBoxes[i].Width + ART_GAP;
        }
        return width;
    }

    // Draws a piece of art as big as it fits in a box, upright, false when there is no picture of it (or not yet, the
    // skin stitches what is asked for into its atlas by the next frame)
    private bool DrawArt(UIDrawList list, UISkin skin, in HidlArt item, UIRect box, Vector4 ink)
    {
        switch (item.Kind)
        {
            case HidlArtKind.Symbol:
                if (!UISymbols.TryGet(item.Name, out char code))
                    return false;
                list.Symbol(code, box, ink);
                return true;

            case HidlArtKind.Icon:
                if (!FindIcon(skin, item, out UIIcon icon, out UISkin iconSkin))
                    return false;
                list.Icon(iconSkin, icon, Fit(box, icon.Region.TexelSize), Vector4.One);
                return true;

            case HidlArtKind.Sprite:
                if (!FindSprite(skin, item, out UIRegion region, out UISkin spriteSkin))
                    return false;
                list.Region(spriteSkin, region, Fit(box, region.TexelSize), Vector4.One);
                return true;

            case HidlArtKind.Picture:
                if (FileOfArt(item.Name) is not { } file || !skin.TryGetImage(file, ART_MOST, out UIRegion picture))
                    return false;
                list.Region(picture, Fit(box, picture.TexelSize), Vector4.One);
                return true;
        }

        return false;
    }

    // How many pixels a piece of art is across and down, nothing for a Codicon (which is square) or what isn't there
    private Vector2 TexelsOf(UISkin skin, in HidlArt item) => item.Kind switch
    {
        HidlArtKind.Picture when FileOfArt(item.Name) is { } file => skin.ImageSize(file),
        HidlArtKind.Sprite when FindSprite(skin, item, out UIRegion region, out _) => region.TexelSize,
        HidlArtKind.Icon when FindIcon(skin, item, out UIIcon icon, out _) => icon.Region.TexelSize,
        _ => Vector2.Zero
    };

    // An icon the code names, out of the skin the code's art is (ArtSkin) or failing that the editor's own, through the
    // set of icons the text said it is written in ([icons:playstation][icon:pad_a] is the cross)
    private bool FindIcon(UISkin skin, in HidlArt item, out UIIcon icon, out UISkin owner)
    {
        ReadOnlySpan<char> set = item.Set.AsSpan();
        if (ArtSkin?.Invoke() is { } art && art.TryGetIcon(item.Name, set, out icon))
        {
            owner = art;
            return true;
        }

        owner = skin;
        return skin.TryGetIcon(item.Name, set, out icon);
    }

    // A piece of a skin's art the code names, the same way round
    private bool FindSprite(UISkin skin, in HidlArt item, out UIRegion region, out UISkin owner)
    {
        if (ArtSkin?.Invoke() is { } art && art.TryGetRegion(item.Name, out region))
        {
            owner = art;
            return true;
        }

        owner = skin;
        return skin.TryGetRegion(item.Name, out region);
    }

    // Where a picture named in the code is, asked of ArtPath once a name
    private string? FileOfArt(string name)
    {
        if (artFiles.TryGetValue(name, out string? file))
            return file;

        file = artPath is { } resolve ? resolve(name) : (File.Exists(name) ? name : null);
        artFiles[name] = file;
        return file;
    }

    // The biggest box of a picture's shape that fits in another, in its middle, in whole pixels
    private static UIRect Fit(UIRect box, Vector2 size)
    {
        if (size.X <= 0.0f || size.Y <= 0.0f)
            return box;

        float fit = MathF.Min(box.Width / size.X, box.Height / size.Y);
        Vector2 drawn = Vector2.Round(size * fit);
        Vector2 corner = Vector2.Round(box.Center - drawn * 0.5f);
        return new UIRect(corner, corner + drawn);
    }

    // A piece of art the pointer is on, big over the code next to where it is, a picture at a whole number of times its
    // pixels when it's small (pixel art stays crisp) and brought down to fit when it isn't
    private void PaintArtLarge(UIDrawList list, UISkin skin, float lineHeight, in HidlArt item, UIRect at)
    {
        float most = MathF.Round(lineHeight * 8.0f);
        Vector2 size = new(most);
        Vector2 texels = TexelsOf(skin, item);

        if (texels.X > 0.0f && texels.Y > 0.0f)
        {
            float times = most / MathF.Max(texels.X, texels.Y);
            size = Vector2.Round(texels * (times >= 1.0f ? MathF.Floor(times) : times));
        }

        // Over the line it's on, or under it when there's no room over it, kept inside the editor from side to side
        const float PAD = 6.0f;
        Vector2 outer = size + new Vector2(PAD * 2.0f);
        float x = Math.Clamp(at.Center.X - outer.X * 0.5f, Bounds.Min.X, MathF.Max(Bounds.Min.X, Bounds.Max.X - outer.X));
        float y = at.Max.Y + 4.0f + outer.Y <= Bounds.Max.Y ? at.Max.Y + 4.0f : at.Min.Y - 4.0f - outer.Y;
        var panel = new UIRect(new Vector2(x, y), new Vector2(x + outer.X, y + outer.Y));

        list.Popup(panel, Module?.Compositor.PopupGlass);
        DrawArt(list, skin, item, panel.Shrink(new UIEdges(PAD)), skin.TextColor);
    }

    // A faint line down every level of indentation, through blank lines as deep as the lines either side of them
    private void PaintGuides(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, int last)
    {
        float space = FontOf(skin).Measure(" ", scale, markup: false).X;
        float tab = space * TAB_SPACES;
        Vector4 guide = skin.TextColor with { W = 0.08f };

        for (int index = first; index <= last; index++)
        {
            int line = rows[index].Line;
            int depth = Indentation(line);
            if (depth < 0)
            {
                // Blank, as deep as the shallower of the nearest lines with something on them, up and down
                int up = -1, down = -1;
                for (int at = line - 1; at >= 0 && at > line - 200 && up < 0; at--) up = Indentation(at);
                for (int at = line + 1; at < document.LineCount && at < line + 200 && down < 0; at++) down = Indentation(at);
                depth = Math.Max(0, Math.Min(up, down));
            }

            float top = RowTop(area, index, lineHeight);
            for (int level = 1; level * TAB_SPACES <= depth; level++)
            {
                float x = MathF.Round(left + (level - 1) * tab);
                list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + 1.0f, top)), guide);
            }
        }
    }

    // How many spaces in a line starts, a tab as four, -1 for a line with nothing on it
    private int Indentation(int line)
    {
        string text = document.LineAt(line);
        int spaces = 0;
        foreach (char character in text)
        {
            if (character == ' ') spaces++;
            else if (character == '\t') spaces += TAB_SPACES;
            else return spaces;
        }

        return -1;
    }

    private void PaintBracket(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, UITextPosition at, int firstLine, int lastLine)
    {
        if (at.Line < firstLine || at.Line > lastLine || IsHidden(at.Line, out _))
            return;

        list.Outline(Piece(skin, scale, area, left, lineHeight, at.Line, at.Column, at.Column + 1), 1.0f, skin.HighlightColor with { W = 0.6f });
    }

    private void PaintDiagnostic(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, in UICodeDiagnostic diagnostic, int firstLine, int lastLine)
    {
        if (diagnostic.Line < firstLine || diagnostic.Line > lastLine || diagnostic.Line >= document.LineCount || IsHidden(diagnostic.Line, out _))
            return;

        int length = document.LineAt(diagnostic.Line).Length;
        int column = Math.Min(diagnostic.Column, length);
        int endColumn = Math.Min(length, column + Math.Max(1, diagnostic.Length));

        UIRect piece = Piece(skin, scale, area, left, lineHeight, diagnostic.Line, column, endColumn);
        float to = MathF.Max(piece.Min.X + lineHeight * 0.5f, piece.Max.X);
        float thickness = MathF.Max(2.0f, MathF.Round(lineHeight * 0.08f));
        Vector4 shade = diagnostic.Warning ? skin.WarningColor : skin.ErrorColor;

        list.Rect(new UIRect(piece.Min, new Vector2(to, piece.Min.Y + thickness)), shade);
    }

    // Whether a line has something wrong on it, for its number in the gutter
    private bool IsMarked(int line, out bool warning)
    {
        warning = false;
        if (SyntaxError is { } error && error.Line == line)
            return true;

        for (int i = 0; i < moreErrors.Count; i++)
        {
            if (moreErrors[i].Line == line)
                return true;
        }

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
