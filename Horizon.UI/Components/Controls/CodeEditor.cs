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
/// <param name="Warning">Whether it's only a warning, or less than that, rather than something wrong.</param>
/// <param name="Hint">
/// Whether it's only worth a look (a language server's hint or information, a variable never used), drawn quieter than
/// a warning, a dotted line in the text's colour and no mark in the gutter. One is a warning as well, for whoever only
/// tells what's wrong apart from what isn't.
/// </param>
/// <param name="Deprecated">Whether it says what it's about is obsolete, struck through the way ReSharper strikes it as well as marked.</param>
public readonly record struct UICodeDiagnostic(int Line, int Column, int Length, string Message, bool Warning = false, bool Hint = false, bool Deprecated = false)
{
    /// <summary>The language server's web documentation for this diagnostic code.</summary>
    public string? DocumentationUrl { get; init; }
    public IReadOnlyList<UICodeDiagnosticLocation>? RelatedLocations { get; init; }
}

/// <summary>A source location the server associates with a diagnostic, such as an earlier declaration.</summary>
public readonly record struct UICodeDiagnosticLocation(string File, int Line, int Column, string Message);

/// <summary>Something a <see cref="CodeEditor"/> offers to write where the caret is, a word it knows.</summary>
/// <param name="Text">What it is, which is what is matched against what has been typed so far.</param>
/// <param name="Detail">A few words about it, written fainter next to it.</param>
/// <param name="Insert">What goes in when it is picked, the text itself unless this says otherwise.</param>
/// <param name="Kind">What kind of thing it is in a language server's words (method, field, keyword), its icon, see UICompletionKinds.</param>
public readonly record struct UICompletion(string Text, string Detail = "", string? Insert = null, string Documentation = "", string Kind = "", IReadOnlyList<UITextEdit>? AdditionalEdits = null, int? ExpectedVersion = null, UITextPosition? ReplacementEnd = null, Func<Task<UICompletionDocumentation?>>? ResolveDocumentation = null, UITextPosition? ReplacementStart = null, int? SelectionStart = null, int SelectionLength = 0);

/// <summary>
/// What there is to offer at the caret, and from which column of the caret's line what is picked replaces what is there.
/// With nothing yet because it's still coming (a language server asked and not answered), Waiting is said in a quiet
/// row of the list after a moment, an answer that comes quickly never shows it.
/// </summary>
public readonly record struct UICompletionRequest(int Start, IReadOnlyList<UICompletion> Items, string Waiting = "");

/// <summary>How a function is called, said over a call being typed, the piece from Start for Length (the argument the caret is on) in bold.</summary>
public readonly record struct UISignature(string Text, int Start, int Length)
{
    /// <summary>The native method documentation, as plain prose for the signature popup.</summary>
    public string Documentation { get; init; } = string.Empty;

    /// <summary>Documentation for the parameter currently being entered, when supplied by the server.</summary>
    public string ParameterDocumentation { get; init; } = string.Empty;

    /// <summary>
    /// Every overload of the call, this one among them, each with its own argument picked out. Null (or one) for a call
    /// with no others. Up and down go through them while it's said, Visual Studio's way.
    /// </summary>
    public IReadOnlyList<UISignature>? Overloads { get; init; }

    /// <summary>Which of the overloads this one is, the one the language server thought best.</summary>
    public int Active { get; init; }
}

/// <summary>A few words written faintly after the end of a line, the value a let came to in Hex (Light Table's inline results).</summary>
public readonly record struct UICodeHint(int Line, string Text);

/// <summary>What happened to some lines since a version they're measured against, the last commit say.</summary>
public enum UILineChangeKind
{
    Added,
    Modified,

    /// <summary>Lines taken out from just before <see cref="UICodeLineChange.Line"/>, which has nothing to colour so it gets a wedge at its top.</summary>
    Deleted,

    /// <summary>Lines that are there to show what was taken out (a diff shows them), a bar of the error colour like added's of success.</summary>
    Removed
}

/// <summary>A run of lines with a colour behind them right across, from a line (0 based) for so many, the lines a diff took out or put in.</summary>
public readonly record struct UICodeLineHighlight(int Line, int Count, Vector4 Colour);
public readonly record struct UICodeOccurrence(int Line, int Column, int Length, int Kind);

/// <summary>A run of lines that changed, from a line (0 based) for so many, drawn as a bar down the gutter beside them.</summary>
public readonly record struct UICodeLineChange(int Line, int Count, UILineChangeKind Kind);

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
    // How many characters wide the room between the line numbers and the code is (the chevron and the change's bar go in
    // it), and the room either side of the code
    private const float GUTTER_GAP = 1.85f;
    private const float TEXT_PAD = 4.0f;

    // How far up from the bottom edge a row cut off by it fades out over, a share of a line
    private const float BOTTOM_FADE = 0.5f;

    // How long after the last change the code is parsed again, how often the caret blinks, and the double click
    private const float CHECK_DELAY = 0.35f;
    private const float BLINK = 0.53f;
    private const double DOUBLE_CLICK = 0.4;

    private const float BAR_WIDTH = UIScrollBar.WIDTH;
    private const int WHEEL_LINES = 3;


    // The list of what can be completed, as many rows as it shows at once and the most it holds
    private const int COMPLETION_ROWS = 10;
    private const int MAX_COMPLETIONS = 200;

    // What is offered at the caret, which of it is picked, which is the first in sight, and where what is picked goes in
    private readonly List<UICompletion> completions = [];
    private int completionIndex, completionStart;
    private float completionTop;

    // The first row of the completions as it's drawn, gliding to completionTop, the way a list's rows do (see ListBox)
    private float completionShown;

    // What the list says while what it offers is still coming, and for how long it's been coming. It shows after a
    // moment, a server that answers at once had the row flash up and go
    private string waiting = string.Empty;
    private float waitedFor;
    private const float WAIT_SHOWN = 0.3f;
    private bool WaitingShown => waiting.Length > 0 && waitedFor >= WAIT_SHOWN;

    // The parts of what is drawn that are counted apart, see UIDrawList.BeginPart. Lines count from 0 on their own
    private const int PART_BACK = -1, PART_OVERLAY = -2, PART_CARET = -3, PART_BAR = -4, PART_SWATCHES = -5, PART_BACKDROP = -6, PART_STICKY = -7, PART_SIGNATURE = -8, PART_ART = -9, PART_GUTTER = 1 << 20;

    // Well clear of the lens rows' parts, which count down from -11 by line
    private const int PART_BRACKETS = -(1 << 30), PART_RULERS = -(1 << 30) + 1;

    // What a row has painted under its text, each a part of the row's own (see RowPart)
    private const int ROW_BEHIND = 1, ROW_GUIDES = 2, ROW_WHITESPACE = 3, ROW_SELECTED = 4;

    // What a squiggle's part is worked out from, with where it is
    private const int PART_SQUIGGLE = 5;

    // How many rows past either edge of what's in sight are painted, out of sight, for the next tick to blend from
    private const int ROWS_AROUND = 3;

    /// <summary>
    /// Helper method for the part one row paints something in, by what the row is (its line and where it starts) the way
    /// its text is. The guides and the dots of the whitespace were all one part for every row in sight, counted down the
    /// screen, so a row scrolled into sight or a selection a row longer had every one after it blended with another's for
    /// a frame, and the blank lines filled up with dots and guides on their way somewhere else.
    /// </summary>
    private static int RowPart(int what, in CodeRow row) => HashCode.Combine(what, row.Line, row.Start, row.Lens, row.Predicted, row.Block) & 0x3FFFFFFF;

    private readonly UITextDocument document = new();

    // Transient IME preedit state shown inline at the caret without mutating the document until commit
    private string compositionText = string.Empty;
    private int compositionCursor;

    /// <summary>Transient IME preedit text shown inline at the caret before commit; does not mutate <see cref="Document"/>.</summary>
    public string CompositionText => compositionText;

    /// <summary>Caret offset within <see cref="CompositionText"/>.</summary>
    public int CompositionCursor => compositionCursor;

    protected override int CompositionPaintStamp => compositionText.Length == 0 ? 0 : HashCode.Combine(compositionText, compositionCursor);

    // The colouring of every line, and the colours written in it. Worked out for the whole file, 1.7 ms and a megabyte for
    // two thousand lines, on a thread of the pool once the text has been left alone RECOLOUR_DELAY (a new text straight
    // away, on the editor's). Until it comes back a line that isn't what was coloured is coloured on its own, from where
    // the last colouring found it starts (FreshColours), it was drawn plain for a tenth of a second. What colours it is
    // the highlighter, HIDL unless the file is another language (CodeLanguages)
    private const float RECOLOUR_DELAY = 0.05f;

    // How many lines a new text has coloured on the spot, a screen or so, the rest is coloured on the pool
    private const int COLOUR_AT_ONCE = 300;

    private List<SyntaxSpan>[] spans = [[]];
    private string[] colouredLines = [];
    private CodeLineStart[] lineStarts = [CodeLineStart.First];
    private ICodeHighlighter highlighter = HidlHighlighter.Instance;
    private readonly List<HidlSwatch> swatches = [];
    private int coloured = -1, changeSeen = -1;
    private float sinceChange;
    private bool recolourNow = true;

    // The colouring going on off the editor's thread, and the lines coloured on their own while it does, by their text
    private sealed record Colouring(int Version, List<SyntaxSpan>[] Spans, string[] Lines, List<HidlSwatch> Swatches, CodeLineStart[] Starts, List<HidlArt> Art, ICodeHighlighter Reader);

    // What the colours there are now were worked out by, null for none (plain text), the next colouring goes on from them
    // when it's the same
    private ICodeHighlighter? colouredWith;

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

    // The line the colours of FreshColours were last asked for, the very string, and them
    private string? freshText;
    private List<SyntaxSpan>? freshSpans;

    // How many letters of a long line (see LongLineSpans) are read on the editor's thread a paint at most, a dozen ms or
    // so in a Debug build, and how far past the last of it in sight it's read, room to scroll into before it's read on
    private const int LONG_BUDGET = 8 * 1024;
    private const int LONG_AHEAD = 4 * 1024;

    // How far into each long line the rows the last paint drew go, by the line, what reading it aims for (LongColours)
    private readonly Dictionary<int, int> longInSight = [];

    // The paint a long line was last read on in (one read a paint), whether a read stopped short of what's in sight, and
    // the last read of a long line by its line and the very string, what the next read of it goes on from
    private int longReadPaint = -1;
    private bool longReading;
    private (int Line, string Text, List<SyntaxSpan> Spans)? longLast;

    // The parse going on off the editor's thread, what it found and for which version of the text
    private sealed record Checked(int Version, UICodeDiagnostic? Error, List<UICodeDiagnostic> More);
    private Task<Checked>? checking;

    // Every other place the word under the caret is, lit faintly, worked out when the caret or the text moves
    private readonly List<UITextPosition> occurrences = [];
    private int occurrenceLength;
    private (int Version, UITextPosition Caret) occurrencesFor = (-1, default);

    // How long the typing has to stop before the word's other places are looked for again. Looked for on every key, a
    // file of twenty thousand lines was gone through a line at a time for every letter typed
    private const float OCCURRENCES_AFTER = 0.1f;
    private int occurrencesVersionSeen = -1;
    private float occurrencesWait;
    private const int MAX_OCCURRENCES = 500;
    private const int OCCURRENCE_SYNC_RADIUS = 400;
    private sealed record OccurrenceScan(int Version, UITextPosition Caret, int Length, List<UITextPosition> Matches);
    private Task<OccurrenceScan>? occurrenceScan;

    // What the hover hint was last asked about, plus a small per-version cache so sweeping the pointer back and forth
    // over already-inspected words doesn't re-invoke HoverSource on every transition.
    private (int Version, int Line, int Start) hoverFor = (-1, -1, -1);
    private string hoverText = string.Empty;
    private readonly Dictionary<(int Line, int Start), string> hoverCache = [];
    private int hoverCacheVersion = -1;
    private const int MAX_HOVER_CACHE = 64;

    // How long the text has to stay the same before the hint is asked again after a change, and how long it has been. A
    // hint can be worked out of the whole file (HIDL's is), and a pointer left resting on the code had a five thousand
    // line .hor parsed for every key typed, sixty milliseconds a letter
    private const float HOVER_AFTER = 0.3f;
    private int hoverVersionSeen = -1;
    private float hoverQuiet;

    // Parsing a moment after the last change, and what came of it
    private int checkedVersion = -1;
    private float checkIn;

    private float scrollY, scrollX;

    // Where the wheel wants the view to be, which it eases to (null while it is where it's wanted). The wheel used to move
    // it three lines in one tick, and the frames between two ticks blend the rows that were in sight both times, so the
    // rows that came into sight sat over the ones still sliding past for a frame, the text flickered as it scrolled.
    // A few pixels a tick nothing comes in over anything
    private float? scrollGoal;

    // The give past the top and the bottom of the code, see UIOverscroll
    private UIOverscroll over;

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

    // When a double click picked a word and on which line, for telling a third press straight after it, which is the line
    private double wordPress = -1.0;
    private int wordPressLine;

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
            linesWere = document.LineCount;
            caretLineWas = 0;
            recolourNow = true;
        }
    }

    /// <summary>
    /// The scale of the text, or zero to use the skin's. Changed (a zoom), the caret's line stays where it was on the
    /// screen, or the line at the top when the caret is out of sight. The scroll is in pixels, so it went on pointing at the
    /// same pixel of a taller or shorter text and the code slid away under the caret.
    /// </summary>
    public float TextScale
    {
        get => textScale;
        set
        {
            if (value == textScale)
                return;

            KeepPlaceThroughZoom();
            textScale = value;
            if (smoothZoomChange)
            {
                scaleSetAt = System.Diagnostics.Stopwatch.GetTimestamp();

                // Keep full frame rate while continuously zooming, and schedule a tick once it rests to snap back to whole pixels
                GameEngine.Instance?.WindowManager?.RequestSmooth();
                GameEngine.Instance?.WindowManager?.RequestTickIn(ZOOM_RESTS_AFTER + 0.02);
            }
            else
            {
                scaleSetAt = 0;
                GameEngine.Instance?.WindowManager?.RequestFrames();
            }
        }
    }

    private float textScale;
    private bool smoothZoomChange;

    // When the scale was last changed, and how long after it a zoom counts as still going on, seconds
    private long scaleSetAt;
    private const double ZOOM_RESTS_AFTER = 0.2;

    /// <summary>Whether the scale was changed a moment ago, a zoom still going on, see ScaleOf.</summary>
    private bool Zooming
    {
        get
        {
            if (scaleSetAt == 0)
                return false;

            if (System.Diagnostics.Stopwatch.GetTimestamp() - scaleSetAt < ZOOM_RESTS_AFTER * System.Diagnostics.Stopwatch.Frequency)
                return true;

            // Rested: once more at whole pixels, kept in place as any zoom is
            scaleSetAt = 0;
            zoomAnchor = null;
            KeepPlaceThroughZoom();
            GameEngine.Instance?.WindowManager?.RequestFrames();
            return false;
        }
    }



    // The row a zoom keeps in the middle of the view (its line, and column for a wrapped one), how far into the row the middle
    // was, a share of it, and the scale before. Null for none
    private (int Line, int Column, float Into, float OldScale)? zoomAnchor;

    // The scale the code was drawn at last, see PaintCode
    private float scaleDrawn;

    /// <summary>Whether the lines are numbered down the left.</summary>
    public bool LineNumbers { get; set; } = true;

    /// <summary>What the first line is numbered, 1 unless it shows a piece of a file from further in (a peek at a declaration).</summary>
    public int FirstLineNumber { get; set; } = 1;

    /// <summary>Whether it can only be read, the caret and the selection still go about it.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Whether the line the caret is on has a faint band behind it while the editor has the keyboard, as code does. A box
    /// for a message has none, a one line box that's all band looked like a field of its own over the box.
    /// </summary>
    public bool CaretBand { get; set; } = true;

    // A soft keyboard comes up for an editor a finger focused, unless nothing can be typed into it
    protected internal override bool WantsTextInput => !ReadOnly;

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

            // Ctrl+/ and Shift+Alt+A write the comments of the language, # in Python, -- in SQL, <!-- --> in HTML
            CodeComment comment = CodeComments.For(value);
            document.LineComment = comment.Line;
            document.BlockComment = (comment.BlockStart, comment.BlockEnd);

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

    /// <summary>Whether completion still belongs to the current external context, such as a paused debugger frame.</summary>
    public Func<bool>? CompletionAvailable { get; set; }

    /// <summary>Additional characters that ask the completion source for suggestions, such as a C++ scope colon.</summary>
    public Func<char, bool>? CompletionTrigger { get; set; }

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
        set
        {
            diagnostics = value ?? [];
            RebuildDiagnosticsSorted();
        }
    }

    private UICodeDiagnostic[] diagnosticsSorted = [];

    private void RebuildDiagnosticsSorted()
    {
        if (diagnostics.Count == 0)
        {
            diagnosticsSorted = [];
            return;
        }

        var sorted = new UICodeDiagnostic[diagnostics.Count];
        for (int i = 0; i < sorted.Length; i++)
            sorted[i] = diagnostics[i];
        Array.Sort(sorted, static (a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
        diagnosticsSorted = sorted;
    }

    private int FirstDiagnosticAtOrAfter(int line)
    {
        int low = 0, high = diagnosticsSorted.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (diagnosticsSorted[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>
    /// What to say about the word the pointer rests on, markdown (what it is, where it was made, what it does), null
    /// or empty for nothing. Asked once for as long as the pointer stays on the same word. What is wrong there is said
    /// first.
    /// </summary>
    public Func<CodeEditor, UITextPosition, string?>? HoverSource
    {
        get => hoverSource;
        set
        {
            if (ReferenceEquals(hoverSource, value))
                return;

            hoverSource = value;
            hoverFor = (-1, -1, -1);
            hoverCache.Clear();
            hoverCacheVersion = -1;
        }
    }

    private Func<CodeEditor, UITextPosition, string?>? hoverSource;

    /// <summary>
    /// How the function being called at the caret is called, asked with its name (dotted, math.floor) and which argument the
    /// caret is on from 0 whenever the caret goes into or along a call, null for nothing to say. Drawn over the call.
    /// </summary>
    public Func<string, int, UISignature?>? SignatureSource { get; set; }

    /// <summary>
    /// What the call at the caret is said to be, the overload up and down went to, null when the caret isn't in one,
    /// nobody had anything to say or escape took it away.
    /// </summary>
    public UISignature? Signature => ShownSignature();

    /// <summary>Which overload of the call is said, from 0, and how many it has, (0, 1) for a call that has just the one.</summary>
    public (int Index, int Count) SignatureOverload =>
        signature is { Overloads: { Count: > 1 } all } said ? (Wrap(said.Active + overloadStep, all.Count), all.Count) : (0, 1);

    /// <summary>What the box over a call says of its overloads in front of it, "1 of 3", the one it is then how many.</summary>
    public string OverloadCounter { get; set; } = "{0} of {1}";

    /// <summary>
    /// Escape with nothing open over the code keeps the keyboard in it, the way every code editor has it. It let go of the
    /// keyboard and what was typed next went nowhere. In a window of its own it's the window's to close.
    /// </summary>
    /// <summary>Whether the hover asked for at the caret is up, which Escape takes down before anything else.</summary>
    public bool HoverUp => hoverPinnedFor is not null;

    protected internal override bool OnEscape()
    {
        // A prediction showing goes first, it's what was put in front of the code last
        if (prediction is not null)
        {
            ClearPrediction();
            return true;
        }

        // Then the hover asked for at the caret
        if (hoverPinnedFor is not null)
        {
            TakeHoverDown();
            return true;
        }

        // Then the box over the call, until the caret's in another call or Ctrl+Shift+Space asks for it again
        if (ShownSignature() is not null)
        {
            signatureHidden = true;
            return true;
        }

        for (UIComponent? at = Parent; at is not null; at = at.Parent)
        {
            if (at is WindowPanel)
                return false;
        }

        return true;
    }

    /// <summary>
    /// What's said of the name at the caret, markdown, in the hover's own box under the caret's line, for the keyboard.
    /// It stays up until the caret moves, the text changes, the pointer moves or Escape. It was a window of its own in
    /// the middle of the screen, over the line it was about.
    /// </summary>
    public void ShowHoverAtCaret(string markdown)
    {
        if (Module is not { } module || markdown.Length == 0)
            return;

        hoverPinnedFor = (document.Version, document.Caret);
        HideSignature();
        float below = paintedSkin is { } skin ? FontOf(skin).LineHeight * paintedScale * 0.5f : 10.0f;
        module.Compositor.PinTooltip(this, markdown, CaretPoint + new Vector2(-8.0f, below), markdown: true);
    }

    // The text's version and the caret the hover was put up at, null while none is
    private (int Version, UITextPosition Caret)? hoverPinnedFor;

    private void TakeHoverDown()
    {
        hoverPinnedFor = null;
        Module?.Compositor.UnpinTooltip(this);
    }

    /// <summary>
    /// The hover and the signature asked for again on the next tick, for a source that answers later than it's asked (a
    /// language server). It says nothing the first time and calls this once it has the answer.
    /// </summary>
    public void AskAgain()
    {
        hoverFor = (-1, -1, -1);
        hoverCache.Clear();
        hoverCacheVersion = -1;
        signatureFor = (-1, default);
    }

    /// <summary>A few words after the end of some lines, faint, by the line. Empty for none.</summary>
    public IReadOnlyList<UICodeHint> InlineHints
    {
        get => inlineHints;
        set
        {
            inlineHints = value ?? [];
            inlineHintsSorted.Clear();
            if (inlineHints.Count > 0)
            {
                if (inlineHintsSorted.Capacity < inlineHints.Count)
                    inlineHintsSorted.Capacity = inlineHints.Count;
                for (int i = 0; i < inlineHints.Count; i++)
                    inlineHintsSorted.Add(inlineHints[i]);
                inlineHintsSorted.Sort(static (a, b) => a.Line.CompareTo(b.Line));
            }
        }
    }

    private IReadOnlyList<UICodeHint> inlineHints = [];
    private readonly List<UICodeHint> inlineHintsSorted = [];

    private int FirstInlineHintAtOrAfter(int line)
    {
        int low = 0, high = inlineHintsSorted.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (inlineHintsSorted[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>
    /// A note about the line the caret is on, fainter than a hint and after it, shown only while the caret is on that
    /// line (who last changed it, git blame). The program keeps it up as the caret moves. Null for none.
    /// </summary>
    public UICodeHint? LineNote { get; set; }

    /// <summary>
    /// How long the caret stays on a line before its note fades in, in seconds. Arrowing down through the code had a
    /// note flash up on every line it went past.
    /// </summary>
    public float LineNoteDelay { get; set; } = 0.5f;

    // How many digits after the end of its line the note starts, and how much of a comment's colour it's drawn in. It was
    // four digits on at half a comment, after a lit bracket it read as more of the code
    private const float LINE_NOTE_GAP = 6.0f, LINE_NOTE_INK = 0.75f;

    // How long the note takes to fade in once it's due, and the line the caret's been on and how long for
    private const float LINE_NOTE_FADE = 0.2f;
    private int lineNoteFor = -1;
    private float lineNoteAge;

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
        if ((uint)line >= (uint)spans.Length || (uint)line >= (uint)colouredLines.Length || (uint)line >= (uint)document.LineCount || colouredLines[line] != document.LineAt(line))
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

    protected internal override UIFont? TooltipCodeFont(UISkin skin) => FontOf(skin);

    /// <summary>Whether guides are drawn down the indentation.</summary>
    public bool IndentGuides { get; set; } = true;

    /// <summary>
    /// How many spaces wide a tab is drawn (the font has no tab of its own) and how far apart the guides are, 1 to 16.
    /// What Tab puts in is the document's <see cref="UITextDocument.Indent"/>, the program sets the two together.
    /// </summary>
    public int TabSize
    {
        get => tabSize;
        set
        {
            int clamped = Math.Clamp(value, 1, 16);
            if (clamped == tabSize)
                return;

            // A tab is wider or narrower, so a wrapped line can wrap somewhere else
            tabSize = clamped;
            rowsFor = rowsFor with { Version = -1 };
        }
    }

    private int tabSize = 4;

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
        PlaceBackdrop(area, Module?.Frame ?? area, picture, BackdropScale, BackdropOffset, BackdropStretch);

    /// <summary>
    /// Where a backdrop goes in an area, its bottom right corner moved in by the offset, the size and the offset both
    /// shares of the window (its height for the size, the picture keeping its shape) rather than of the picture's pixels.
    /// </summary>
    public static UIRect PlaceBackdrop(UIRect area, UIRect window, Horizon.Graphics.Texture picture, float scale, Vector2 offset)
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
    /// How the breakpoints that aren't plain are drawn, by their line, a condition's dot, a message's diamond, the outline
    /// of one that's off. A line with a breakpoint and nothing here has the plain red dot. Null for all plain.
    /// </summary>
    public IReadOnlyDictionary<int, CodeBreakpointMark>? BreakpointMarks { get; set; }

    /// <summary>Counted up by whoever changes <see cref="BreakpointMarks"/>, so the gutter is drawn again for it.</summary>
    public int BreakpointMarksVersion { get; set; }

    /// <summary>
    /// A press in the gutter's strip for the marks, given the line pressed, a breakpoint put down or taken up. Set, a dot
    /// shows faintly under the pointer there. Null and a press in the gutter selects the line, as it always did.
    /// </summary>
    public Action<int>? OnGutterPressed { get; set; }

    /// <summary>The line a debugger stopped on, lit right across and an arrow in the gutter, -1 for none.</summary>
    public int ExecutionLine { get; set; } = -1;

    /// <summary>
    /// The lines that changed since some version of them (git's), a bar down the gutter between the numbers and the code,
    /// the skin's success colour for added, warning for changed, and a wedge of the error colour where lines were taken
    /// out. The program works them out and keeps them up as the text changes, the editor only shows them. Empty for none.
    /// </summary>
    public IReadOnlyList<UICodeLineChange> LineChanges
    {
        get => lineChanges;
        set
        {
            lineChanges = value ?? [];
            changedLines.Clear();
            deletedBefore.Clear();
            foreach (UICodeLineChange change in lineChanges)
            {
                if (change.Kind == UILineChangeKind.Deleted)
                    deletedBefore.Add(change.Line);
                else
                    for (int line = change.Line; line < change.Line + change.Count; line++)
                        changedLines[line] = change.Kind;
            }
        }
    }

    private IReadOnlyList<UICodeLineChange> lineChanges = [];

    // The same by the line, for painting a row at a time
    private readonly Dictionary<int, UILineChangeKind> changedLines = [];
    private readonly HashSet<int> deletedBefore = [];

    /// <summary>A press on the bar or the wedge of a change in the gutter, given the change. Null and it selects the line as ever.</summary>
    public Action<UICodeLineChange>? OnChangePressed { get; set; }

    /// <summary>Runs of lines with a colour behind them right across, under the selection, the lines of a diff. Empty for none.</summary>
    public IReadOnlyList<UICodeLineHighlight> LineHighlights
    {
        get => lineHighlights;
        set
        {
            lineHighlights = value ?? [];
            highlightedLines.Clear();
            foreach (UICodeLineHighlight highlight in lineHighlights)
                for (int line = highlight.Line; line < highlight.Line + highlight.Count; line++)
                    highlightedLines[line] = highlight.Colour;
        }
    }

    private IReadOnlyList<UICodeLineHighlight> lineHighlights = [];
    private readonly Dictionary<int, Vector4> highlightedLines = [];

    // The colour of a change's bar, in the gutter and down the minimap
    private static Vector4 ChangeColour(UILineChangeKind kind, UISkin skin) => kind switch
    {
        UILineChangeKind.Added => skin.Palette.Success,
        UILineChangeKind.Removed => skin.ErrorColor,
        _ => skin.WarningColor
    };

    /// <summary>Every place the word under the caret is, which are lit faintly.</summary>
    public IReadOnlyList<UITextPosition> Occurrences => occurrences;
    public Func<UITextPosition, IReadOnlyList<UICodeOccurrence>?>? OccurrenceSource { get; set; }
    public IReadOnlyList<UICodeOccurrence> NativeOccurrences => nativeOccurrences;
    private IReadOnlyList<UICodeOccurrence> nativeOccurrences = [];
    public void RefreshOccurrences() => occurrencesFor = (document.Version, new UITextPosition(-1, -1));

    /// <summary>
    /// Whether the other places of the word under the caret are lit. Off for text that's only shown, where the caret sits
    /// at the start and the first word was lit all down it (every "tick" of a loop's printing in a notebook).
    /// </summary>
    public bool LightOccurrences
    {
        get => lightOccurrences;
        set
        {
            if (lightOccurrences == value)
                return;

            lightOccurrences = value;
            nativeOccurrences = [];
            occurrences.Clear();
            occurrenceLength = 0;
            occurrencesFor = (-1, default);
        }
    }

    private bool lightOccurrences = true;

    /// <summary>
    /// Called with the notches of the wheel turned while control is held, to zoom the code (away from the user is in).
    /// Null, the wheel scrolls whatever is held.
    /// </summary>
    public Action<float>? OnZoom { get; set; }

    /// <summary>A pinch of a trackpad zooms by this factor (1.02 is two per cent bigger), where <see cref="OnZoom"/> is given steps of a wheel.</summary>
    public Action<float>? OnPinch { get; set; }

    /// <summary>A smart zoom (two finger double tap) over the editor, the zoom goes between the usual size and a bigger one.</summary>
    public Action? OnSmartZoom { get; set; }

    protected internal override bool OnMagnify(float amount)
    {
        if (OnPinch is not { } pinch)
            return false;

        bool wasSmooth = smoothZoomChange;
        smoothZoomChange = true;
        try
        {
            pinch(Horizon.Input.PreciseScroll.PinchFactor(amount));
        }
        finally
        {
            smoothZoomChange = wasSmooth;
        }
        return true;
    }

    protected internal override bool OnSmartMagnify()
    {
        if (OnSmartZoom is not { } smart)
            return false;

        smart();
        return true;
    }
    protected internal override bool UsesScrollSpeed => GameEngine.Instance?.Input.Keyboard.Primary != true;

    protected internal override bool HasOverscroll => over.Active;

    protected internal override bool? ScrollEnd(float delta)
    {
        if (paintedSkin is not { } skin || AllInSight || completions.Count > 0)
            return null;

        float most = MaxScroll(skin, paintedScale);
        return most <= 0.0f ? null : over.Active ? false : delta > 0.0f ? scrollY <= 0.5f : scrollY >= most - 0.5f;
    }

    // A finger on the bar or the minimap drags that, anywhere else it scrolls the code
    protected internal override bool KeepsTouchDrag(Vector2 point) => (paintedSkin is { } skin && point.X >= TextArea(skin, paintedScale).Max.X) || base.KeepsTouchDrag(point);

    /// <summary>
    /// Called as Tab is pressed with one caret and nothing selected, before it indents. True when it took the key (an
    /// abbreviation before the caret written out) and nothing is indented. Null, Tab only ever indents.
    /// </summary>
    public Func<bool>? OnTab { get; set; }

    /// <summary>
    /// Called as Enter is pressed with Control held, true when it took the key and no line is started. For an editor that
    /// is a box of text with a button after it (a commit's message), Control and Enter being the button. Null, it's a new line.
    /// </summary>
    public Func<bool>? OnControlEnter { get; set; }

    /// <summary>Offered Enter after an open completion list; true submits a code prompt instead of inserting a line.</summary>
    public Func<bool>? OnEnter { get; set; }

    /// <summary>
    /// Offered Up (-1) and Down (1) before they move the caret, every repeat of a held key as well; true when it took the
    /// key. For a list of the owner's over the box, which a shortcut moved once a press while the caret went too.
    /// </summary>
    public Func<int, bool>? OnUpDown { get; set; }

    /// <summary>Called after every change of the text.</summary>
    public Action? OnChanged { get; set; }

    /// <summary>A character typed into the document, after paired characters and indentation; Enter is a newline.</summary>
    public Action<char>? OnTyped { get; set; }

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
            nativeFoldingRanges = null;
            checkIn = CHECK_DELAY;
            revealCaret = true;
            PredictionTextChanged();
            if (!ReadOnly && IsShownInHierarchy)
                GameEngine.Instance?.WindowManager?.NoteInput();
            OnChanged?.Invoke();
        };
        document.LinesMoved += MoveDiagnostics;
        document.LinesMoved += MoveInlays;
        document.LinesMoved += MoveLensesAbove;
        document.LinesMoved += MoveBlockSpaces;
    }

    public CodeEditor(string text)
        : this()
    {
        Text = text;
    }

    protected override bool HitTestVisible => true;
    protected internal override bool Focusable => true;
    protected internal override bool KeepsTab => !TabMovesFocus;

    /// <summary>
    /// Whether Tab goes on to the next thing rather than indenting, VS Code's tab focus mode. For code shown read only
    /// inside something else (a peek), which Tab got into and never out of again.
    /// </summary>
    public bool TabMovesFocus { get; set; }

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

    /// <summary>
    /// The scale the code is drawn and measured at: the one asked for, a hair bigger or smaller so a character of a
    /// fixed-width font is a whole number of the screen's pixels. A cell of 8.4 pixels put every column at another share
    /// of a pixel, and the same letter came out crisp in one column and split over two in the next, a line of code
    /// wobbling along. Everything of the editor goes by this scale, so the caret, the selection and the guides follow.
    /// </summary>
    private float ScaleOf(UISkin skin)
    {
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        // A zoom on its way goes as smoothly as it's asked to, and lands on whole pixels once it has stopped. Rounded every
        // tick the code went up and down in steps while the zoom's own number went smoothly
        if (Zooming)
            return scale;

        UIFont font = FontOf(skin);
        float cell = font.Measure("M", 1.0f, markup: false).X;
        if (cell <= 0.0f || MathF.Abs(font.Measure("i", 1.0f, markup: false).X - cell) > 0.01f)
            return scale;

        float pixels = cell * scale * PixelsPerUnit;
        return pixels < 3.0f ? scale : scale * MathF.Round(pixels) / pixels;
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

    private float TabWidth(UISkin skin, float scale) => FontOf(skin).Measure(" ", scale, markup: false).X * TabSize;

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

    // How many letters of a row are drawn or measured at a time when it's longer, see DrawInSight
    private const int DRAWN_PIECE = 512;

    /// <summary>
    /// Draws a piece of a row the way <see cref="DrawWithInlays"/> does, but only what is between two lefts on screen
    /// (the code in sight and a width either side of it), and says where the piece ends as far as it got. A long piece goes
    /// a few hundred letters at a time, measured while it's left of what's in sight and given up once it's past the right.
    /// A line of a fifth of a megabyte was drawn whole every paint, every letter of it clipped.
    /// </summary>
    private float DrawInSight(UIDrawList list, UISkin skin, float scale, int line, int column, ReadOnlySpan<char> text, float x, float top, Vector4 color, float left, float origin, UISyntaxColors colors, float from, float to)
    {
        if (text.Length <= DRAWN_PIECE && x >= from)
            return DrawWithInlays(list, skin, scale, line, column, text, x, top, color, left, origin, colors);

        int at = 0;
        while (at < text.Length && x <= to)
        {
            // Never between the two halves of a letter outside the basic plane
            int end = Math.Min(text.Length, at + DRAWN_PIECE);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
                end--;

            ReadOnlySpan<char> piece = text[at..end];
            float past = x < from ? DrawWithInlays(list, skin, scale, line, column + at, piece, x, top, color, left, origin, colors, draw: false) : float.NaN;
            x = past < from ? past : DrawWithInlays(list, skin, scale, line, column + at, piece, x, top, color, left, origin, colors);
            at = end;
        }

        return x;
    }

    /// <summary>Helper method to snapshot the document's line references without joining them on the UI thread.</summary>
    private string[] SnapshotLines()
    {
        int count = document.LineCount;
        var lines = new string[count];
        for (int i = 0; i < count; i++)
            lines[i] = document.LineAt(i);
        return lines;
    }

    /// <summary>Helper method to join only the first <paramref name="count"/> lines for bounded synchronous work.</summary>
    private string JoinFirstLines(int count)
    {
        count = Math.Min(count, document.LineCount);
        if (count <= 0)
            return string.Empty;
        if (count == 1)
            return document.LineAt(0);

        int total = count - 1;
        for (int i = 0; i < count; i++)
            total += document.LineAt(i).Length;

        var builder = new System.Text.StringBuilder(total);
        builder.Append(document.LineAt(0));
        for (int i = 1; i < count; i++)
            builder.Append('\n').Append(document.LineAt(i));
        return builder.ToString();
    }

    private static void EnsureSwatchesSorted(List<HidlSwatch> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Line < list[i - 1].Line)
            {
                list.Sort(static (a, b) => a.Line.CompareTo(b.Line));
                return;
            }
        }
    }

    private static void EnsureArtSorted(List<HidlArt> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Line < list[i - 1].Line)
            {
                list.Sort(static (a, b) => a.Line.CompareTo(b.Line));
                return;
            }
        }
    }

    /// <summary>The colours of every line worked out again on the editor's thread, and the lines kept to tell later which still fit them.</summary>
    private void Recolour()
    {
        coloured = changeSeen = document.Version;
        recolourNow = false;
        fresh.Clear();
        freshText = null;

        // New arrays, a colouring going on in the background may be reading the last ones
        int count = document.LineCount;
        lineStarts = new CodeLineStart[count];
        colouredWith = Highlight ? highlighter : null;

        int firstLong = Highlight ? FirstLongLine(Math.Min(count, COLOUR_AT_ONCE)) : -1;
        if (Highlight && (count > COLOUR_AT_ONCE || firstLong >= 0))
        {
            // A big file has its top coloured now and the rest plain, all of it coloured on a thread of the pool and taken
            // in when it's done. Reading a 100,000 line file through the grammar on the spot held the window up for nine
            // seconds before it showed a thing. A line too long to read on the spot stops the top there, what of it is
            // in sight is read a paint at a time meanwhile (LongColours)
            int now = firstLong >= 0 ? firstLong : COLOUR_AT_ONCE;
            spans = PlainSpans(longLines: true);
            if (now > 0)
            {
                List<SyntaxSpan>[] top = highlighter.Read(JoinFirstLines(now), now, lineStarts, swatches, art);
                Array.Copy(top, spans, Math.Min(Math.Min(top.Length, now), spans.Length));
            }

            colouredWith = null;
            coloured = document.Version - 1;
        }
        else if (Highlight && (count == 0 || (count == 1 && document.LineAt(0).Length == 0)))
        {
            // Nothing to colour, and nothing asked of the grammar. An empty box made the grammar get going on the spot, the
            // agents' box was Markdown's (and every language it can hold) at 70 ms and 24 MB of an editor's start for one empty line
            spans = PlainSpans();
        }
        else if (Highlight)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            spans = highlighter.Read(JoinFirstLines(count), count, lineStarts, swatches, art);
            if (Horizon.Core.Diagnostics.StartupTrace.On && !Horizon.Core.Diagnostics.StartupTrace.SceneReady)
                Horizon.Logging.Log.Info($"[Startup] {highlighter.Name} coloured {count} lines in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms");
        }
        else
        {
            spans = PlainSpans();
            swatches.Clear();
            art.Clear();
        }

        EnsureSwatchesSorted(swatches);
        EnsureArtSorted(art);
        TookArt();

        colouredLines = SnapshotLines();
        FindFoldable();
    }

    // The colours worked out on a thread of the pool, from the text as it is now, taken in by Update when they're done
    private void StartColouring()
    {
        int version = document.Version, count = document.LineCount;
        string[] lines = SnapshotLines();

        changeSeen = version;
        ICodeHighlighter reader = highlighter;

        // Gone on from the last colouring when it was this highlighter's, only the lines from the edit on read again
        CodeColouring? last = ReferenceEquals(colouredWith, reader) ? new CodeColouring(colouredLines, spans, lineStarts) : null;
        colouring = Task.Run(() =>
        {
            // Join off the UI thread so typing in large files never allocates multi-megabyte strings on the sim thread.
            string text = string.Join('\n', lines);
            var found = new List<HidlSwatch>();
            var named = new List<HidlArt>();
            var starts = new CodeLineStart[count];
            List<SyntaxSpan>[] read = last is null ? reader.Read(text, count, starts, found, named) : reader.ReadAgain(text, lines, starts, last, found, named);
            EnsureSwatchesSorted(found);
            EnsureArtSorted(named);
            var result = new Colouring(version, read, lines, found, starts, named, reader);
            GameEngine.Instance?.WindowManager?.Wake();
            return result;
        });
    }

    private void TakeColouring(Colouring done)
    {
        spans = done.Spans;
        colouredLines = done.Lines;
        lineStarts = done.Starts;
        colouredWith = done.Reader;
        swatches.Clear();
        swatches.AddRange(done.Swatches);
        art.Clear();
        art.AddRange(done.Art);
        TookArt();
        coloured = done.Version;
        fresh.Clear();
        freshText = null;
        FindFoldable();
    }

    /// <summary>
    /// The colours of a line typed into since the last colouring, worked out for it alone and kept by its text until the
    /// next one comes back. Null for a line the last colouring never saw the start of.
    /// </summary>
    private List<SyntaxSpan>? FreshColours(int line, string text)
    {
        if (!Highlight || text.Length == 0 || lineStarts.Length == 0)
            return null;
        if (text.Length > TextMateHighlighter.LONG_LINE)
            return LongColours(line, text);

        // The line asked for again as it was, every row of it in a paint. Looked up by its text it was hashed whole each
        // time, a long line's every row went through all of it
        if (ReferenceEquals(text, freshText))
            return freshSpans;

        if (fresh.TryGetValue(text, out var known))
        {
            (freshText, freshSpans) = (text, known);
            return known;
        }

        // Where the line was in the last colouring, or where the lines added or taken out above it moved it from
        int shifted = line - (document.LineCount - colouredLines.Length);
        int was = document.LineCount != colouredLines.Length && (uint)shifted < (uint)lineStarts.Length
            ? shifted
            : (uint)line < (uint)lineStarts.Length ? line : shifted;
        if ((uint)was >= (uint)lineStarts.Length)
            return null;

        if (fresh.Count >= MAX_FRESH)
            fresh.Clear();

        if (!highlighter.TryReadLine(text, lineStarts[was], out var read) || read is null)
        {
            GameEngine.Instance?.WindowManager.RequestTickIn(0.016f);
            return null;
        }
        fresh[text] = read;
        (freshText, freshSpans) = (text, read);
        return read;
    }

    /// <summary>
    /// The colours of a line, null when it was typed into since they were worked out. A line is looked for where it was and
    /// where it was before the lines added or taken out above it moved it, so Enter doesn't take the colours off the rest.
    /// </summary>
    private List<SyntaxSpan>? ColoursOf(int line, string text)
    {
        List<SyntaxSpan>? found = null;
        if ((uint)line < (uint)colouredLines.Length && (uint)line < (uint)spans.Length && colouredLines[line] == text)
        {
            found = spans[line];
        }
        else
        {
            int was = line - (document.LineCount - colouredLines.Length);
            if ((uint)was < (uint)colouredLines.Length && (uint)was < (uint)spans.Length && colouredLines[was] == text)
                found = spans[was];
        }

        // A long line not read to its end yet is read on by LongColours
        return found is LongLineSpans { Whole: false } ? null : found;
    }

    /// <summary>
    /// The colours of a line too long to read whole on the spot (see <see cref="LongLineSpans"/>), read from its start a
    /// piece at a time as far as a little past what of it is in sight, going on from the last read of it, LONG_BUDGET
    /// letters a paint at most. Until it's that far the editor paints every tick (see PaintVersion), so what's in sight is
    /// coloured a little more each one, and scrolled along it's read on again. Typed into, it's read again from just
    /// before the edit and the rest taken over from the last read once the grammar is where it was (TextMateHighlighter).
    /// The pool reads all of it meanwhile and that takes over when it's done.
    /// </summary>
    private List<SyntaxSpan>? LongColours(int line, string text)
    {
        List<SyntaxSpan>? have = ReferenceEquals(text, freshText) ? freshSpans : fresh.GetValueOrDefault(text);
        int goal = longInSight.TryGetValue(line, out int seen) ? Math.Min(text.Length, seen + LONG_AHEAD) : 0;
        if (have is not null && (have is not LongLineSpans { ReadTo: var readSoFar } || readSoFar >= goal || longReadPaint == flowPaint))
        {
            if (have is LongLineSpans { ReadTo: var shortOf } && shortOf < goal)
                longReading = true;
            (freshText, freshSpans) = (text, have);
            return have;
        }

        // Where the line was in the last colouring, or where the lines added or taken out above it moved it from
        int shifted = line - (document.LineCount - colouredLines.Length);
        int was = document.LineCount != colouredLines.Length && (uint)shifted < (uint)lineStarts.Length
            ? shifted
            : (uint)line < (uint)lineStarts.Length ? line : shifted;
        if ((uint)was >= (uint)lineStarts.Length)
            return have;

        // Gone on from the read of this text so far, else (it was typed into) whichever got further of the line's last read
        // here and the colouring's, which has all of it once the pool is done
        var (from, fromText) = have is not null ? (have, text)
            : (uint)was < (uint)colouredLines.Length && (uint)was < (uint)spans.Length ? (spans[was], colouredLines[was]) : ((List<SyntaxSpan>?)null, (string?)null);
        if (have is null && longLast is { } last && last.Line == line && ReadSoFar(last.Spans) > ReadSoFar(from))
            (from, fromText) = (last.Spans, last.Text);

        // A highlighter that can't reads all of it, as any line
        List<SyntaxSpan>? read = highlighter.ReadUpTo(text, lineStarts[was], goal, LONG_BUDGET, from, fromText);
        if (read is null && (!highlighter.TryReadLine(text, lineStarts[was], out read) || read is null))
        {
            GameEngine.Instance?.WindowManager.RequestTickIn(0.016f);
            return null;
        }

        longReadPaint = flowPaint;
        if (read is LongLineSpans { ReadTo: var reached } && reached < goal)
            longReading = true;

        if (fresh.Count >= MAX_FRESH)
            fresh.Clear();
        fresh[text] = read;
        (freshText, freshSpans) = (text, read);
        longLast = (line, text, read);
        return read;
    }

    // How far into its line a read of a long line got, -1 for colours that weren't read that way
    private static int ReadSoFar(List<SyntaxSpan>? read) => read is LongLineSpans { ReadTo: var to } ? to : -1;

    // Helper method for the first of so many lines too long to read on the spot, -1 for none
    private int FirstLongLine(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (document.LineAt(i).Length > TextMateHighlighter.LONG_LINE)
                return i;
        }

        return -1;
    }

    // Every line all one span, for text that isn't coloured as code, or a long line nothing of which is read yet
    private List<SyntaxSpan>[] PlainSpans(bool longLines = false)
    {
        var plain = new List<SyntaxSpan>[document.LineCount];
        for (int i = 0; i < plain.Length; i++)
        {
            int length = document.LineAt(i).Length;
            plain[i] = longLines && length > TextMateHighlighter.LONG_LINE ? new LongLineSpans(length)
                : length > 0 ? [new SyntaxSpan(0, length, SyntaxKind.Name)] : [];
        }

        return plain;
    }

    private UIRect TextArea(UISkin skin, float scale)
    {
        UIRect content = Bounds.Shrink(Padding);
        return new UIRect(new Vector2(content.Min.X + GutterWidth(skin, scale), content.Min.Y), new Vector2(content.Max.X - BAR_WIDTH - MinimapWidth, content.Max.Y));
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

        // A row of a prediction's isn't in the text, a point on it is the end of the line it's under
        if (row.Predicted > 0)
            return new UITextPosition(line, text.Length);

        float wanted = point.X - (area.Min.X + TEXT_PAD - scrollX) - row.Indent;
        if (wanted <= 0.0f || row.Start >= rowEnd)
            return new UITextPosition(line, row.Start);

        UIFont font = FontOf(skin);
        float tabWidth = TabWidth(skin, scale);
        List<UICodeInlay>? lineInlays = InlaysOn(line);
        int inlayIdx = 0;
        if (lineInlays is not null)
        {
            while (inlayIdx < lineInlays.Count && lineInlays[inlayIdx].Column < row.Start)
                inlayIdx++;
        }

        // Single-pass O(N) advance accumulation matching Width + InlaysWidth; a click on an inlay lands before its character.
        float textWidth = 0.0f;
        float inlaySum = 0.0f;
        char prevChar = '\0';
        bool hasPrev = false;
        for (int col = row.Start; col < rowEnd; col++)
        {
            if (lineInlays is not null)
            {
                while (inlayIdx < lineInlays.Count && lineInlays[inlayIdx].Column == col)
                {
                    inlaySum += InlayWidth(skin, scale, lineInlays[inlayIdx]);
                    inlayIdx++;
                }
            }

            float charLeft = textWidth + inlaySum;
            char current = text[col];
            float step;
            if (current == '\t')
            {
                step = tabWidth;
                hasPrev = false;
            }
            else
            {
                float kern = hasPrev ? font.Kerning(prevChar, current) * scale : 0.0f;
                step = kern + font.Measure(text.AsSpan(col, 1), scale, markup: false).X;
                prevChar = current;
                hasPrev = true;
            }

            float charRight = charLeft + step;
            if (wanted < (charLeft + charRight) * 0.5f)
                return new UITextPosition(line, col);

            textWidth += step;
        }

        // Past the end of a row that isn't the last of its line is the start of the next, which is the same place
        return new UITextPosition(line, rowEnd);
    }

    /// <summary>Where a place in the text was last drawn, the left of its column halfway down its row, the other way round to <see cref="PositionAt"/>.</summary>
    public Vector2 PlaceOf(UITextPosition at)
    {
        if (paintedSkin is not { } skin || document.LineCount == 0)
            return default;

        float scale = paintedScale;
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        EnsureRows(skin, scale);

        int line = Math.Clamp(at.Line, 0, document.LineCount - 1);
        float x = area.Min.X + TEXT_PAD - scrollX + ColumnX(skin, scale, line, at.Column);
        return new Vector2(x, TopOf(area, line, at.Column, lineHeight) - lineHeight * 0.5f);
    }

    protected override Vector2 Measure(UISkin skin) => FitRows > 0 ? FittedSize(skin) : new Vector2(480, 320) + Padding.Total;

    /* Updating */

    protected override void Update(float dt)
    {
        // The list of completions goes with the keyboard, it stayed up over whatever took it (a palette, a window)
        if (CompletionOpen && (!IsFocused || CompletionAvailable?.Invoke() == false))
            CloseCompletion();
        UpdateCompletionDocumentation();

        // Control and Enter types nothing (UIKeyboard leaves it to the program's shortcuts), so a box with a button after
        // it hears it here. It never got to OnTextInput, and Ctrl+Enter in a commit's message did nothing at all
        if (OnControlEnter is not null && IsFocused && !ReadOnly && GameEngine.Instance?.Input.Keyboard is { Primary: true } keys
            && (keys.WasPressed(Horizon.Input.Key.Enter) || keys.WasPressed(Horizon.Input.Key.KeypadEnter)))
        {
            CloseCompletion();
            OnControlEnter();
        }

        // The hover at the caret goes once the caret or the text moves on, or something else took it down
        if (hoverPinnedFor is { } pinnedAt && (pinnedAt != (document.Version, document.Caret) || Module?.Compositor.Pinned(this) != true))
            TakeHoverDown();
        UpdatePrediction();

        // How long the caret's been on the line the note is about, from nothing again every time it goes to another line
        int noted = LineNote is { } note && note.Line == document.Caret.Line ? note.Line : -1;
        lineNoteAge = noted == lineNoteFor ? lineNoteAge + dt : 0.0f;
        lineNoteFor = noted;

        // The caret blinks only while the window has the keyboard, out of it the caret stays lit and still, so a window left
        // in the background isn't painted again twice a second for a caret nobody's typing at. Lit and still as well with
        // the UI's motion turned off, a caret that blinks is motion too
        bool still = MotionSpeed <= 0.0f;
        blink = still || !IsFocused || GameEngine.Instance?.WindowManager is { IsFocused: false } ? 0.0f : (blink + dt) % (BLINK * 2.0f);
        if (!still && IsFocused && GameEngine.Instance?.WindowManager is { IsFocused: true } blinkingWindow)
            blinkingWindow.RequestTickIn(BLINK - blink % BLINK);
        tickTime = dt;
        if (waiting.Length > 0)
            waitedFor += dt;
        flowTime += dt;

        float scrollDt = ScrollMotionDelta(dt);

        // The completions' rows on the way to the ones picked, a fraction of a row at a time
        if (completionShown != completionTop)
        {
            completionShown += (completionTop - completionShown) * (1.0f - MathF.Exp(-SCROLL_EASE * scrollDt));
            if (MathF.Abs(completionTop - completionShown) < 0.01f)
                completionShown = completionTop;
            else
                GameEngine.Instance?.WindowManager?.RequestSmooth();
        }

        over.Step(scrollDt, MotionSpeed);

        // On the way to where the wheel wants it
        if (scrollGoal is { } goal)
        {
            scrollY += (goal - scrollY) * (1.0f - MathF.Exp(-SCROLL_EASE * scrollDt));
            if (MathF.Abs(goal - scrollY) < 0.5f)
            {
                scrollY = goal;
                scrollGoal = null;
            }
            else
            {
                GameEngine.Instance?.WindowManager?.RequestSmooth();
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
        UpdateLensesHeld();

        if (bracketFor != (document.Version, document.Caret))
            FindBracket();

        if (occurrenceScan is { IsCompleted: true } scanned)
        {
            occurrenceScan = null;
            if (scanned.IsCompletedSuccessfully && scanned.Result.Version == document.Version && scanned.Result.Caret == document.Caret)
            {
                occurrences.Clear();
                if (scanned.Result.Matches.Count >= 2)
                {
                    occurrences.AddRange(scanned.Result.Matches);
                    occurrenceLength = scanned.Result.Length;
                }
                else
                {
                    occurrenceLength = 0;
                }
            }
        }

        if (occurrencesFor != (document.Version, document.Caret))
            UpdateOccurrences(dt);

        if (signatureFor != (document.Version, document.Caret))
            FindSignature();

        RefreshFound();

        if (revealCaret && paintedSkin is { } skin)
            Reveal(skin);

        UpdateTooltip();
        UpdateMiddleScroll(dt);
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
        string[] lines = SnapshotLines();
        checkedVersion = version;
        checking = Task.Run(() =>
        {
            var result = Parse(version, string.Join('\n', lines));
            GameEngine.Instance?.WindowManager?.Wake();
            return result;
        });
    }

    private static Checked Parse(int version, string text)
    {
        UICodeDiagnostic? error = null;
        var more = new List<UICodeDiagnostic>();

        static UICodeDiagnostic Said(ParseException e) => new(Math.Max(0, e.Line - 1), Math.Max(0, e.Col - 1), Math.Max(0, e.Length), e.Message);

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

    /// <summary>Reveal the current caret after its host changes the available editing area.</summary>
    public void RevealCaret(bool center = false)
    {
        revealCaret = true;
        centreOnReveal = center;
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
        float lensTop = top - LensRoomAbove(caret.Line, caret.Column, lineHeight);
        // Pinned scopes cover the top of the text area. Leave one extra row for their
        // rounded-to-row bottom edge, including when a compact find bar shrinks the view.
        int pinned = StickyLines(caret.Line).Count;
        float covered = pinned == 0 ? 0 : MathF.Min(MathF.Max(0, area.Height - lineHeight), (pinned + 1) * lineHeight);

        // The caret has the say, wherever the wheel was taking it. A line or a few it went off the edge are eased into
        // sight the way the wheel's are, a page or more away is gone to at once. A line at a time it jumped, and the
        // lines smeared across each jump
        float from = scrollGoal ?? scrollY;
        if (AllInSight)
        {
            // All of it shows once it's grown to fit, and a new line is revealed before it has. Scrolled to it then, the
            // code stayed a line up and out of the top of a box already as tall as it, nothing clamped it back after
            centreOnReveal = false;
            scrollGoal = null;
            scrollY = 0.0f;
        }
        else if (centreOnReveal)
        {
            centreOnReveal = false;
            scrollGoal = null;
            scrollY = top - covered - (area.Height - covered - lineHeight) * 0.4f;
        }
        else if ((lensTop < from + covered ? lensTop - covered : top + lineHeight > from + area.Height ? top + lineHeight - area.Height : (float?)null) is { } wanted)
        {
            if (MathF.Abs(wanted - scrollY) > area.Height)
            {
                scrollGoal = null;
                scrollY = wanted;
            }
            else
            {
                scrollGoal = wanted;
            }
        }

        if (revealBlockOwner is not null)
            RevealBlock(skin, scale, area, lineHeight);

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

    /// <summary>How far down the code is scrolled in pixels, set it goes there at once, the way a drag of the bar does. For the checks.</summary>
    public float ScrollPixels
    {
        get => scrollY;
        set
        {
            scrollGoal = null;
            scrollY = MathF.Max(0.0f, value);
        }
    }

    /// <summary>How far right the code is scrolled in pixels, set it goes there at once. For the checks.</summary>
    public float ScrollXPixels
    {
        get => scrollX;
        set => scrollX = MathF.Max(0.0f, value);
    }

    /// <summary>Whether it's on its way somewhere the wheel or the caret sent it. For the checks.</summary>
    internal bool Easing => scrollGoal is not null || revealCaret;

    // How far down the code goes
    private float MaxScroll(UISkin skin, float scale)
    {
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        return MathF.Max(0.0f, Math.Max(1, rows.Count) * lineHeight - area.Height * (FitRows > 0 ? 1.0f : 0.5f));
    }

    private void ClampScroll(UISkin skin, float scale)
    {
        float most = MaxScroll(skin, scale);

        // Past the end because the code got shorter under it (lines taken out, a row of counts gone), eased back rather
        // than put there. Put there, the whole view jumped the next time the caret moved
        if (scrollY > most && scrollGoal is null)
            scrollGoal = most;
        scrollY = MathF.Max(0.0f, scrollY);
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

    /// <summary>
    /// The caret over to the pair of the bracket next to it, the same side of it as it was of the first one, so going
    /// again comes back. False with no bracket by the caret, or one whose pair is too far off to find.
    /// </summary>
    public bool GoToMatchingBracket()
    {
        if (bracketFor != (document.Version, document.Caret))
            FindBracket();
        if (bracket is not { } from || bracketPair is not { } to)
            return false;

        bool after = document.Caret.Column > from.Column;
        document.MoveTo(new UITextPosition(to.Line, to.Column + (after ? 1 : 0)), false);
        revealCaret = true;
        return true;
    }

    // The tooltip says what is wrong where the pointer rests, and nothing anywhere else
    private void UpdateTooltip()
    {
        hoveredArt = -1;
        if (hoverVersionSeen != document.Version)
        {
            hoverVersionSeen = document.Version;
            hoverQuiet = 0.0f;
        }
        else
        {
            hoverQuiet += tickTime;
        }

        if (!IsHovered || Module is not { } module || paintedSkin is null)
        {
            if (Tooltip.Length > 0) Tooltip = string.Empty;
            return;
        }

        // A piece of art at the end of a line is shown big, and says nothing
        Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
        if (AgentHintTooltip(pointer)) return;
        for (int i = 0; i < art.Count && i < artBoxes.Length; i++)
        {
            if (artBoxes[i].Width > 0.0f && artBoxes[i].Shrink(new UIEdges(-2.0f)).Contains(pointer))
            {
                hoveredArt = i;
                if (Tooltip.Length > 0) Tooltip = string.Empty;
                return;
            }
        }

        // A lens row over a line has none of the line on it to say anything about
        if (OverLensRow(pointer))
        {
            if (Tooltip.Length > 0) Tooltip = string.Empty;
            return;
        }

        // Who is on the line, in the gutter; said with whatever else there is to say there, not instead of it
        string remoteNames = RemoteGutterTooltip(pointer);

        UITextPosition at = PositionAt(pointer);
        string said = string.Empty;

        // What the program has to say about the word, asked once a word, under what's wrong there when something is
        bool hint = false;
        if (HoverSource is { } source)
        {
            var (start, end) = document.WordAt(at);

            // The text changed under a word that was asked about, nothing until the typing stops, what it said may be wrong now
            bool typing = hoverFor.Version >= 0 && hoverFor.Version != document.Version && hoverQuiet < HOVER_AFTER;
            if (typing)
            {
                hoverText = string.Empty;
            }
            else if (end > start && at.Column < document.LineAt(at.Line).Length)
            {
                if (hoverFor != (document.Version, at.Line, start))
                {
                    hoverFor = (document.Version, at.Line, start);
                    if (hoverCacheVersion != document.Version)
                    {
                        hoverCache.Clear();
                        hoverCacheVersion = document.Version;
                    }

                    if (!hoverCache.TryGetValue((at.Line, start), out string? cached))
                    {
                        if (hoverCache.Count >= MAX_HOVER_CACHE)
                            hoverCache.Clear();
                        hoverCache[(at.Line, start)] = cached = source(this, new UITextPosition(at.Line, start)) ?? string.Empty;
                    }

                    hoverText = cached;
                }

                said = hoverText;
                hint = said.Length > 0;
            }
        }

        // Something wrong there is a card of it, with what was said of the word under it (DiagnosticCard). By index in
        // there, a foreach over the interface is an enumerator made every tick the pointer is over the code
        if (WrongAt(at))
        {
            said = DiagnosticCard(at, said);
            hint = true;
        }

        if (remoteNames.Length > 0)
        {
            string combined = said.Length > 0 ? remoteNames + "\n\n" + said : remoteNames;
            TooltipMarkdown = hint;
            if (!string.Equals(Tooltip, combined, StringComparison.Ordinal))
                Tooltip = combined;
            return;
        }

        TooltipMarkdown = hint;
        if (!ReferenceEquals(said, Tooltip))
            Tooltip = said;
    }

    // Every place the word under the caret is, whole words only, unless something is selected
    /// <summary>
    /// Helper method for the word's other places, looked for at once when the caret moved and a moment after the typing
    /// stops when the text changed. What was lit goes as soon as the text changes, it would be in the wrong places.
    /// </summary>
    private void UpdateOccurrences(float dt)
    {
        if (occurrencesFor.Version == document.Version)
        {
            FindOccurrences();
            return;
        }

        if (occurrencesVersionSeen != document.Version)
        {
            occurrencesVersionSeen = document.Version;
            occurrencesWait = OCCURRENCES_AFTER;
            nativeOccurrences = [];
            occurrences.Clear();
            occurrenceLength = 0;
            return;
        }

        if ((occurrencesWait -= dt) <= 0.0f)
            FindOccurrences();
    }

    private void FindOccurrences()
    {
        nativeOccurrences = [];
        occurrenceScan = null;
        occurrencesFor = (document.Version, document.Caret);
        occurrences.Clear();
        occurrenceLength = 0;

        if (document.HasSelection || !Highlight || !lightOccurrences)
            return;

        UITextPosition caret = document.Caret;
        if (OccurrenceSource?.Invoke(caret) is { } supplied)
        {
            nativeOccurrences = supplied;
            foreach (var occurrence in supplied) occurrences.Add(new(occurrence.Line, occurrence.Column));
            return;
        }
        var (start, end) = document.WordAt(caret);
        if (end - start < 2)
            return;

        string line = document.LineAt(caret.Line);
        ReadOnlySpan<char> word = line.AsSpan(start, end - start);
        if (char.IsDigit(word[0]))
            return;

        int lineCount = document.LineCount;
        int syncFrom = 0, syncTo = lineCount - 1;
        bool bounded = lineCount > OCCURRENCE_SYNC_RADIUS * 2 + 1;
        if (bounded)
        {
            syncFrom = Math.Max(0, caret.Line - OCCURRENCE_SYNC_RADIUS);
            syncTo = Math.Min(lineCount - 1, caret.Line + OCCURRENCE_SYNC_RADIUS);
        }

        ScanOccurrencesInRange(document, null, syncFrom, syncTo, word, occurrences);

        // The one place it is, the one under the caret, is no help lit
        if (occurrences.Count < 2)
            occurrences.Clear();
        else
            occurrenceLength = word.Length;

        // For large documents, complete the full-file scan asynchronously off the UI thread so cursor moves never stall.
        if (bounded && occurrences.Count < MAX_OCCURRENCES)
            StartOccurrenceScan(document.Version, caret, word);
    }

    private void StartOccurrenceScan(int version, UITextPosition caret, ReadOnlySpan<char> word)
    {
        string[] snapshot = SnapshotLines();
        string wordText = word.ToString();
        occurrenceScan = Task.Run(() =>
        {
            var all = new List<UITextPosition>();
            ScanOccurrencesInRange(null, snapshot, 0, snapshot.Length - 1, wordText.AsSpan(), all);
            var result = new OccurrenceScan(version, caret, wordText.Length, all);
            GameEngine.Instance?.WindowManager?.Wake();
            return result;
        });
    }

    private static void ScanOccurrencesInRange(UITextDocument? doc, string[]? snapshot, int startLine, int endLine, ReadOnlySpan<char> word, List<UITextPosition> results)
    {
        int wordLen = word.Length;
        for (int at = startLine; at <= endLine && results.Count < MAX_OCCURRENCES; at++)
        {
            string text = snapshot is not null ? snapshot[at] : doc!.LineAt(at);
            if (text.Length < wordLen)
                continue;

            ReadOnlySpan<char> span = text.AsSpan();
            for (int from = span.IndexOf(word, StringComparison.Ordinal); from >= 0 && results.Count < MAX_OCCURRENCES;)
            {
                int after = from + wordLen;
                bool whole = (from == 0 || !IsWordChar(span[from - 1])) && (after >= span.Length || !IsWordChar(span[after]));
                if (whole)
                    results.Add(new UITextPosition(at, from));

                int next = span[after..].IndexOf(word, StringComparison.Ordinal);
                from = next < 0 ? -1 : after + next;
            }
        }
    }

    private int FirstFoundAtOrAfter(int line)
    {
        int low = 0, high = found.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (found[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private int FirstOccurrenceAtOrAfter(int line)
    {
        int low = 0, high = occurrences.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (occurrences[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private static bool IsWordChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    /* The keyboard */

    public override void OnComposition(string text, int cursor, int length)
    {
        if (ReadOnly)
            return;

        compositionText = text ?? string.Empty;
        compositionCursor = Math.Clamp(cursor, 0, compositionText.Length);
        blink = 0.0f;
        revealCaret = true;
    }

    public override void OnFocusLost()
    {
        compositionText = string.Empty;
        compositionCursor = 0;
    }

    protected internal override void OnTextInput(char character)
    {
        compositionText = string.Empty;
        compositionCursor = 0;

        char? typed = null;
        middleScroll.Cancel();
        blink = 0.0f;
        revealCaret = true;

        // A prediction has Tab, Ctrl+Right and Escape before the rest (the completion list keeps them while it's open)
        if (prediction is not null && PredictionKey(character))
            return;

        if (CompletionOpen && CompletionKey(character))
            return;

        if (character == UIKeyboard.COMPLETE)
        {
            Complete(typed: false);
            return;
        }

        if (character == UIKeyboard.PARAMETER_INFO)
        {
            ShowSignature();
            return;
        }

        // Up and down offered to whoever has a list over the box first (a chat's / commands), held keys repeating here
        if (character is UIKeyboard.UP or UIKeyboard.DOWN && OnUpDown?.Invoke(character == UIKeyboard.UP ? -1 : 1) == true)
            return;

        // Up and down go through a call's overloads while it has more than one, Visual Studio's way
        if (character is UIKeyboard.UP or UIKeyboard.DOWN && StepOverload(character == UIKeyboard.UP ? -1 : 1))
            return;

        switch (character)
        {
            // A box selection grown from the keyboard, Visual Studio's shift and alt with an arrow
            case UIKeyboard.BOX_UP:
                document.ExtendBox(-1, 0);
                return;
            case UIKeyboard.BOX_DOWN:
                document.ExtendBox(1, 0);
                return;
            case UIKeyboard.BOX_LEFT:
                document.ExtendBox(0, -1);
                return;
            case UIKeyboard.BOX_RIGHT:
                document.ExtendBox(0, 1);
                return;

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
            case UIKeyboard.SELECT_ALL_OCCURRENCES:
                document.SelectAllOccurrences();
                return;
            case UIKeyboard.SELECT_LINE:
                document.ExpandLineSelection();
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
            // A Mac's control keys (see UIKeyboard.CocoaMeaning), the caret's own line and nobody else's
            case UIKeyboard.KILL_LINE:
                if (document.KillToLineEnd() is { Length: > 0 } killed)
                    UIKeyboard.KillBuffer = killed;
                break;
            case UIKeyboard.YANK:
                if (UIKeyboard.KillBuffer.Length > 0) document.Insert(UIKeyboard.KillBuffer);
                break;
            case UIKeyboard.TRANSPOSE: document.Transpose(); break;
            case UIKeyboard.OPEN_LINE: document.OpenLine(); break;
            case UIKeyboard.ENTER when OnControlEnter is not null && GameEngine.Instance.Input.Keyboard.Primary && OnControlEnter(): break;
            // Alt+Enter is a command's key in every editor (Properties in Hex, a quick fix elsewhere), never a new line. It
            // put one in the file as it brought the Properties up
            case UIKeyboard.ENTER when GameEngine.Instance.Input.Keyboard.Alt: break;
            case UIKeyboard.ENTER:
                if (OnEnter?.Invoke() != true) { document.NewLine(); typed = '\n'; }
                break;
            case UIKeyboard.TAB:
                if (document.HasSelection || document.HasExtraCarets || OnTab?.Invoke() != true)
                    IndentOrTab();
                break;
            case UIKeyboard.SHIFT_TAB: document.Outdent(); break;
            case UIKeyboard.UNDO: document.Undo(); break;
            case UIKeyboard.REDO: document.Redo(); break;
            case UIKeyboard.TOGGLE_COMMENT: document.ToggleComment(); break;
            case UIKeyboard.TOGGLE_BLOCK_COMMENT: document.ToggleBlockComment(); break;
            case UIKeyboard.DUPLICATE: document.Duplicate(); break;
            case UIKeyboard.MOVE_LINES_UP: document.MoveLines(-1); break;
            case UIKeyboard.MOVE_LINES_DOWN: document.MoveLines(1); break;
            case UIKeyboard.COPY_LINES_UP: document.CopyLines(-1); break;
            case UIKeyboard.COPY_LINES_DOWN: document.CopyLines(1); break;

            case UIKeyboard.CUT:
                UIKeyboard.Copy(CopiedText());
                if (!document.HasSelection && !document.HasExtraCarets)
                    SelectLine(document.Caret.Line);
                document.Delete();
                break;

            default:
                if (!UIKeyboard.IsEditingKey(character) && !char.IsControl(character))
                {
                    if (prediction is not null)
                        PreparePredictionTyping(character);
                    int version = document.Version;
                    document.Type(character);
                    if (prediction is not null && document.Version == version)
                        FinishPredictionTyping(steppedOver: true);
                    typed = character;

                    // A word being typed (or a member, or a key of a text) is offered what it could be
                    if (StartsCompletion(character))
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
            else if (!StartsCompletion(character))
                CloseCompletion();
        }
        if (typed is {} inserted) OnTyped?.Invoke(inserted);
    }

    /* Completing what is typed */

    private bool StartsCompletion(char character) => UITextDocument.IsWordChar(character) || character is '.' or '@'
        || CompletionTrigger?.Invoke(character) == true;

    /// <summary>
    /// Opens (or narrows down) the list of what the word at the caret could be. Typed, it only opens for a word that has
    /// been started and something that isn't it already, asked for (control and space) it opens whatever.
    /// </summary>
    public void Complete(bool typed = false)
    {
        if (CompletionAvailable?.Invoke() == false) { CloseCompletion(); return; }
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
        bool afterMark = request.Start > 0 && (line[request.Start - 1] is '.' or '@'
            || CompletionTrigger?.Invoke(line[request.Start - 1]) == true);
        if (typed && prefix.Length == 0 && !afterMark)
        {
            CloseCompletion();
            return;
        }

        // Nothing yet because it's still coming, the list is open with a row that says so, the clock going on from the
        // first time it was asked for while the word is typed on
        if (request.Items.Count == 0 && request.Waiting.Length > 0)
        {
            if (waiting.Length == 0)
                waitedFor = 0.0f;
            waiting = request.Waiting;
            completionStart = request.Start;
            completionIndex = 0;
            completionTop = 0.0f;
            completionShown = 0.0f;
            OpenPopup();
            return;
        }

        waiting = string.Empty;

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
        completionShown = 0.0f;
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
        completionDocumentation = null;
        completionDocumentationFor = null;
        waiting = string.Empty;
        if (CompletionOpen)
            ClosePopup();
    }

    /// <summary>What the list of completions says while what it offers is still coming, empty when it isn't waiting.</summary>
    public string CompletionWaiting => waiting;

    /// <summary>Puts what is picked in the list where the word being typed is.</summary>
    public void AcceptCompletion()
    {
        if (CompletionAvailable?.Invoke() == false) { CloseCompletion(); return; }
        if (!CompletionOpen || completionIndex < 0 || completionIndex >= completions.Count)
            return;

        UICompletion picked = completions[completionIndex];
        UITextPosition caret = document.Caret;
        string insert = picked.Insert ?? picked.Text;
        if (picked.SelectionStart is {} selected && (selected < 0 || selected > insert.Length || picked.SelectionLength < 0 || picked.SelectionLength > insert.Length - selected)) return;

        CloseCompletion();
        if (picked.AdditionalEdits is { Count: > 0 } || picked.ReplacementStart is not null || picked.ReplacementEnd is not null || picked.SelectionStart is not null)
        {
            if (picked.ExpectedVersion is { } version && version != document.Version)
            {
                Complete();
                return;
            }
            UITextPosition start = picked.ReplacementStart ?? (caret with { Column = completionStart });
            UITextPosition end = picked.ReplacementEnd ?? caret;
            if (start.Line != caret.Line || start.Column < 0 || start > caret || end.Line != caret.Line || end < caret)
                return;
            document.ApplyCompletion(new(start, end, insert), picked.AdditionalEdits ?? [], picked.SelectionStart, picked.SelectionLength);
            revealCaret = true;
            return;
        }
        document.Select(caret with { Column = completionStart }, caret);
        document.Insert(picked.Insert ?? picked.Text);
        revealCaret = true;
    }

    // The keys that go to the list while it is open, true for one it took
    private bool CompletionKey(char character)
    {
        // Only waiting, the list has nothing to go along or pick, the key does what it does without it
        if (completions.Count == 0)
        {
            if (character == UIKeyboard.COMPLETE)
                return true;
            if (character is UIKeyboard.UP or UIKeyboard.DOWN or UIKeyboard.PAGE_UP or UIKeyboard.PAGE_DOWN or UIKeyboard.ENTER or UIKeyboard.TAB)
                CloseCompletion();
            return false;
        }

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
        if (completions.Count == 0)
            return;

        completionIndex = Math.Clamp(completionIndex + by, 0, completions.Count - 1);
        if (completionIndex < completionTop) completionTop = completionIndex;
        if (completionIndex >= completionTop + COMPLETION_ROWS) completionTop = completionIndex - COMPLETION_ROWS + 1;
    }

    // The language's own words and every name in the code, for an editor nobody told what else there is
    private static readonly string[] Keywords = ["let", "const", "func", "if", "else", "while", "do", "for", "in", "return", "break", "continue", "delete", "true", "false", "null", "vec", "include"];
    private const int MAX_DEFAULT_COMPLETION_LINES = 2000;
    private const int MAX_DEFAULT_COMPLETION_LINE_CHARS = 4096;
    private (int Version, int From, int To) defaultCompletionsFor = (-1, -1, -1);
    private readonly List<UICompletion> defaultCompletionCache = [];

    private static UICompletionRequest? DefaultCompletions(CodeEditor editor)
    {
        UITextDocument doc = editor.Document;
        int start = doc.Caret.Column - doc.WordBeforeCaret().Length;

        int lineCount = doc.LineCount;
        int fromLine = 0, toLine = lineCount - 1;
        if (lineCount > MAX_DEFAULT_COMPLETION_LINES)
        {
            int half = MAX_DEFAULT_COMPLETION_LINES / 2;
            fromLine = Math.Max(0, doc.Caret.Line - half);
            toLine = Math.Min(lineCount - 1, fromLine + MAX_DEFAULT_COMPLETION_LINES - 1);
        }

        var key = (doc.Version, fromLine, toLine);
        if (editor.defaultCompletionsFor != key)
        {
            editor.defaultCompletionsFor = key;
            List<UICompletion> items = editor.defaultCompletionCache;
            items.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string keyword in Keywords)
            {
                if (seen.Add(keyword))
                    items.Add(new UICompletion(keyword, "keyword", Kind: "keyword"));
            }

            // Scan identifiers directly per line without joining the whole document on the UI thread.
            for (int line = fromLine; line <= toLine; line++)
            {
                string text = doc.LineAt(line);
                int limit = Math.Min(text.Length, MAX_DEFAULT_COMPLETION_LINE_CHARS);
                int i = 0;
                while (i < limit)
                {
                    char c = text[i];
                    if (c == '/' && i + 1 < limit && text[i + 1] == '/')
                        break;
                    if (c is '"' or '\'')
                    {
                        char quote = c;
                        i++;
                        while (i < limit && text[i] != quote)
                        {
                            if (text[i] == '\\') i++;
                            i++;
                        }
                        i++;
                        continue;
                    }
                    if (char.IsLetter(c) || c == '_')
                    {
                        int idStart = i++;
                        while (i < limit && IsWordChar(text[i]))
                            i++;
                        if (i - idStart > 1)
                        {
                            string id = text[idStart..i];
                            if (seen.Add(id))
                                items.Add(new UICompletion(id, "in this file", Kind: "variable"));
                        }
                        continue;
                    }
                    i++;
                }
            }
        }

        return new UICompletionRequest(start, editor.defaultCompletionCache);
    }

    // Where the list is, under the start of the word being completed, or over it when there is no room under it
    // How tall a row of the completions is, a line of code with room round it and never less than a row of a list, and the
    // room round the rows inside the popup. A line of code to a row was a list too cramped to read, its text against the edge
    private float CompletionRow(UISkin skin) => MathF.Max(FontOf(skin).LineHeight * paintedScale + 8.0f, 22.0f);
    private const float COMPLETION_PAD = 6.0f;

    // The room in front of the completions' words for the icon of their kind (UICompletionKinds), a line's height and a
    // gap, none when none of the rows in sight says its kind
    private const float COMPLETION_ICON_GAP = 6.0f;
    private float CompletionIconRoom(float lineHeight)
    {
        int start = Math.Max(0, (int)MathF.Floor(completionTop) - 1);
        int last = Math.Min(completions.Count, (int)MathF.Ceiling(completionTop) + COMPLETION_ROWS + 1);
        for (int i = start; i < last; i++)
        {
            if (completions[i].Kind.Length > 0)
                return MathF.Round(lineHeight * 0.9f) + COMPLETION_ICON_GAP;
        }

        return 0.0f;
    }

    private UIRect CompletionBounds()
    {
        if (paintedSkin is not { } skin || (completions.Count == 0 && !WaitingShown))
            return default;

        float scale = paintedScale;
        float lineHeight = FontOf(skin).LineHeight * scale;
        float rowHeight = CompletionRow(skin);
        UIRect area = TextArea(skin, scale);
        UITextPosition caret = document.Caret;

        float x = area.Min.X + TEXT_PAD - scrollX + ColumnX(skin, scale, caret.Line, completionStart);
        float below = TopOf(area, caret.Line, caret.Column, lineHeight) - lineHeight;

        float widest = completions.Count == 0 ? FontOf(skin).Measure(waiting, scale * 0.9f, markup: false).X : 0.0f;
        int start = Math.Clamp((int)MathF.Floor(completionTop), 0, Math.Max(0, completions.Count - 1));
        int last = Math.Min(completions.Count, (int)MathF.Ceiling(completionTop) + COMPLETION_ROWS);
        for (int i = start; i < last; i++)
        {
            var item = completions[i];
            float text = FontOf(skin).Measure(item.Text, scale, markup: false).X;
            float detail = item.Detail.Length > 0 ? FontOf(skin).Measure(item.Detail, scale * 0.85f, markup: false).X + lineHeight : 0.0f;
            widest = MathF.Max(widest, text + detail);
        }

        float width = widest + TEXT_PAD * 4.0f + COMPLETION_PAD * 2.0f + CompletionIconRoom(lineHeight);
        float height = Math.Clamp(completions.Count, 1, COMPLETION_ROWS) * rowHeight + COMPLETION_PAD * 2.0f;

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

    // The part the row that says it's waiting is painted as, apart from the rows' own
    private const int WAITING_PART = -2;

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
        UISyntaxColors colors = CodeLanguages.ColorsFor(Highlighter, skin.Palette);

        // On what every popup sits on, coming in out of the line it's for
        bool entering = list.BeginEntrance(ref completionsOpenedAt, new Vector2(bounds.Min.X, bounds.Max.Y), new Vector2(0.0f, 4.0f));
        list.Popup(bounds, Module?.Compositor.PopupGlass);

        float rowHeight = CompletionRow(skin);

        // Still coming, a quiet row that says so in place of the rows
        if (completions.Count == 0)
        {
            list.BeginPart(WAITING_PART);
            var row = new UIRect(new Vector2(bounds.Min.X + COMPLETION_PAD, bounds.Min.Y + COMPLETION_PAD), new Vector2(bounds.Max.X - COMPLETION_PAD, bounds.Max.Y - COMPLETION_PAD));
            list.Text(waiting, new UIRect(new Vector2(row.Min.X + TEXT_PAD, row.Min.Y), row.Max), Horizon.Rendering.Origin.Left, scale * 0.9f, colors.Comment, markup: false);
            list.EndParts();
            if (entering)
                list.EndEntrance();
            return;
        }

        // From where the glide has got to, the row half out at the top cut off at the list's edge, one more at the bottom
        int first = Math.Clamp((int)MathF.Floor(completionShown), 0, completions.Count - 1);
        int last = Math.Min(completions.Count, first + COMPLETION_ROWS + 1);
        list.PushClip(new UIRect(new Vector2(bounds.Min.X, bounds.Min.Y + COMPLETION_PAD), new Vector2(bounds.Max.X, bounds.Max.Y - COMPLETION_PAD)));

        float iconRoom = CompletionIconRoom(lineHeight);
        for (int i = first; i < last; i++)
        {
            list.BeginPart(i);
            float rowTop = bounds.Max.Y - COMPLETION_PAD - (i - completionShown) * rowHeight;
            var row = new UIRect(new Vector2(bounds.Min.X + COMPLETION_PAD, rowTop - rowHeight), new Vector2(bounds.Max.X - COMPLETION_PAD, rowTop));

            // The picked one on a pill the way every list has it, always painted so the quads don't shift
            list.RoundRect(row, MathF.Min(skin.CornerRadius, 6.0f), skin.TextColor with { W = i == completionIndex ? ListBox.PICKED : 0.0f });

            // The text in the middle of its row
            float top = MathF.Round(rowTop - (rowHeight - lineHeight) * 0.5f);
            var item = completions[i];
            Vector4 ink = colors.Of(UICompletionKinds.SyntaxOf(item.Kind));
            if (iconRoom > 0.0f && UICompletionKinds.IconOf(item.Kind) is { Length: > 0 } icon)
            {
                float side = iconRoom - COMPLETION_ICON_GAP;
                float middle = MathF.Round(row.Center.Y);
                list.Symbol(icon, new UIRect(new Vector2(row.Min.X + TEXT_PAD, middle - side * 0.5f), new Vector2(row.Min.X + TEXT_PAD + side, middle + side * 0.5f)), ink);
            }
            list.Text(item.Text, new Vector2(row.Min.X + TEXT_PAD + iconRoom, top), scale, ink, markup: false);

            if (item.Detail.Length > 0)
            {
                float detail = FontOf(skin).Measure(item.Detail, scale * 0.85f, markup: false).X;
                // Dim the way every list's detail is, in the comments' colour it read as code of the file
                list.Text(item.Detail, new Vector2(row.Max.X - TEXT_PAD - detail, top - lineHeight * 0.08f), scale * 0.85f, skin.DimTextColor, markup: false);
            }
        }

        list.PopClip();
        list.EndParts();
        PaintCompletionDocs(list, skin, scale, bounds, colors);

        if (entering)
            list.EndEntrance();
    }

    // How wide the box of what the picked completion does is, in lines of the code's height, and how many lines it says
    private const float DOCS_WIDTH = 22.0f;
    private const int DOCS_LINES = 10;
    private readonly List<(int Start, int Length)> docsLines = [];

    // The part the box of what the picked completion does is painted as
    private const int DOCS_PART = -3;

    /// <summary>
    /// What the picked completion does, the way VS Code has it beside the list, in a box of its own to the right of it
    /// (to the left when there's no room), as many lines as fit. Nothing for an item a server said nothing about.
    /// </summary>
    private void PaintCompletionDocs(UIDrawList list, UISkin skin, float scale, UIRect bounds, UISyntaxColors colors)
    {
        if (completionIndex < 0 || completionIndex >= completions.Count || completions[completionIndex].Documentation is not { Length: > 0 } said)
            return;

        // In the UI's font, it's prose, and the code's had no dash for what serve-d says it throws
        UIFont font = skin.Font;
        float small = scale * 0.9f;
        float lineHeight = font.LineHeight * small;
        float width = MathF.Round(FontOf(skin).LineHeight * scale * DOCS_WIDTH);
        docsLines.Clear();
        font.Wrap(said, small, width - TEXT_PAD * 2.0f, docsLines, markup: false);
        if (docsLines.Count > DOCS_LINES)
            docsLines.RemoveRange(DOCS_LINES, docsLines.Count - DOCS_LINES);

        float height = docsLines.Count * lineHeight + COMPLETION_PAD * 2.0f + TEXT_PAD;
        float left = Module is { } module && bounds.Max.X + width + 6.0f > module.Frame.Max.X ? bounds.Min.X - width - 6.0f : bounds.Max.X + 6.0f;
        var box = new UIRect(new Vector2(left, bounds.Max.Y - height), new Vector2(left + width, bounds.Max.Y));

        list.BeginPart(DOCS_PART);
        list.Popup(box, Module?.Compositor.PopupGlass);
        var inside = new UIRect(new Vector2(box.Min.X + TEXT_PAD, box.Min.Y + COMPLETION_PAD), new Vector2(box.Max.X - TEXT_PAD, box.Max.Y - COMPLETION_PAD - TEXT_PAD * 0.5f));
        list.PushFont(font);
        list.Text(said, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(docsLines), inside, Horizon.Rendering.Origin.TopLeft, small, skin.DimTextColor, markup: false);
        list.PopFont();
        list.EndParts();
    }

    /// <summary>
    /// Asked first with what control with V brought, true when it took the paste itself (a picture made an attachment of
    /// a message), false to have the text put in as usual.
    /// </summary>
    public Func<string, bool>? OnPasting { get; set; }

    protected internal override bool TakesEmptyPaste => OnPasting is not null;

    protected internal override void OnPaste(string text)
    {
        if (ReadOnly || OnPasting?.Invoke(text) == true || text.Length == 0)
            return;
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

    // The line a triple click picked, while the press goes on dragging whole lines from it, null otherwise
    private int? lineDrag;

    // Whole lines from one to another, either way round, the caret left at the end the pointer is at
    private void SelectLines(int from, int to)
    {
        int first = Math.Min(from, to), last = Math.Max(from, to);
        var start = new UITextPosition(first, 0);
        UITextPosition end = last + 1 < document.LineCount ? new UITextPosition(last + 1, 0) : new UITextPosition(last, document.LineAt(last).Length);
        if (to < from)
            document.Select(end, start);
        else
            document.Select(start, end);
    }

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
        UIRect track = new(new Vector2(area.Max.X + MinimapWidth, area.Min.Y), new Vector2(area.Max.X + MinimapWidth + BAR_WIDTH, area.Max.Y));
        float total = Math.Max(1, rows.Count) * lineHeight;
        float length = MathF.Max(UIScrollBar.MIN_THUMB, track.Height * area.Height / MathF.Max(1.0f, total));
        float most = MathF.Max(1.0f, total - area.Height * 0.5f);
        float top = track.Max.Y - (track.Height - length) * Math.Clamp(scrollY / most, 0.0f, 1.0f);
        return (track, MathF.Min(length, track.Height), most, top);
    }

    // Whether the press was taken by something on the code rather than by the text (a lens, a swatch, the lines held at
    // the top, a chevron), so dragging off it selects nothing. A reference count clicked selected from the caret to it
    // the moment the pointer shook on the way up
    private bool pressTaken;

    protected internal override void OnPointerDown(Vector2 point)
    {
        blink = 0.0f;
        pressTaken = true;

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
            float rowHeight = CompletionRow(listSkin);
            int row = (int)MathF.Floor(completionShown + (listed.Max.Y - COMPLETION_PAD - point.Y) / rowHeight);
            if (row >= 0 && row < completions.Count)
            {
                completionIndex = row;
                AcceptCompletion();
            }
            return;
        }

        CloseCompletion();
        wordDrag = null;
        lineDrag = null;

        // The minimap, its slider taken hold of or the code gone to where it was pressed
        if (PressMinimap(point))
            return;

        if (paintedSkin is { } skin && point.X >= TextArea(skin, paintedScale).Max.X + MinimapWidth)
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

        // A lens after the end of a line is the program's, ahead of the caret going there
        if (PressLens(point))
            return;

        // A press on a lens row that isn't on a lens is between two of them, never the line under it
        if (OverLensRow(point))
            return;

        // A change's bar is the program's to say what to do with (show what changed), ahead of the chevron it's next to
        if (PressLineChange(point, at))
            return;

        // In the gutter a press on a chevron folds, anywhere else in it is the whole line
        if (PressFold(point, at))
            return;

        pressTaken = false;

        if (paintedSkin is { } painted && point.X < TextArea(painted, paintedScale).Min.X)
        {
            // The strip the marks go in, as wide as one of them, is the lightbulb's on its line and otherwise the
            // breakpoints' when somebody wants it
            if (PressAction(point))
                return;
            if (OnGutterPressed is not null && point.X < BreakpointStripRight(painted))
            {
                OnGutterPressed(at.Line);
                return;
            }

            SelectLine(at.Line);
            return;
        }

        // A third press straight after a double click, on the line the word was on, is the whole line, and dragged on
        // from there whole lines. Where in the line doesn't matter, the word a double click picked is under the pointer
        // but the pointer needn't be on the very same letter three times running
        if (wordPress >= 0.0 && now - wordPress < DOUBLE_CLICK && at.Line == wordPressLine)
        {
            SelectLine(at.Line);
            wordDrag = null;
            lineDrag = at.Line;
            wordPress = lastPress = -1.0;
            return;
        }

        wordPress = -1.0;
        if (now - lastPress < DOUBLE_CLICK && at == lastPressAt)
        {
            document.SelectWordAt(at);
            wordDrag = (document.Anchor, document.Caret);
            lastPress = -1.0;
            wordPress = now;
            wordPressLine = at.Line;
            return;
        }

        lastPress = now;
        lastPressAt = at;

        // Alt puts down another caret, alt and shift dragged makes a column of them. With Visual Studio's keys alt dragged
        // makes the column and it's control and alt that puts down a caret
        var keyboard = GameEngine.Instance.Input.Keyboard;
        bool visualStudio = UIKeyboard.VisualStudioKeys;
        if (keyboard.Alt && (keyboard.Shift || visualStudio && !keyboard.Primary))
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
        if (changePressed || pressTaken && !draggingBar && !draggingMinimap)
            return;

        if (draggingMinimap)
        {
            DragMinimap(point);
            return;
        }

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
        else if (lineDrag is { } line)
            SelectLines(line, PositionAt(point).Line);
        else if (wordDrag is { } word)
            document.SelectWordsTo(word.From, word.To, PositionAt(point));
        else if (!document.HasExtraCarets)
            document.MoveTo(PositionAt(point), selecting: true);
        revealCaret = true;
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        draggingBar = false;
        draggingMinimap = false;
        changePressed = false;
        pressTaken = false;
        wordDrag = null;
        lineDrag = null;
        columnFrom = null;
    }

    protected internal override bool OnScroll(float delta)
    {
        middleScroll.Cancel();
        if (OnZoom is { } zoom && GameEngine.Instance?.Input.Keyboard.Primary == true)
        {
            if (PreciseWheel)
            {
                bool wasSmooth = smoothZoomChange;
                smoothZoomChange = true;
                try
                {
                    zoom(Horizon.Input.PreciseScroll.ZoomSteps(delta, true));
                }
                finally
                {
                    smoothZoomChange = wasSmooth;
                }
            }
            else
            {
                zoom(Horizon.Input.PreciseScroll.ZoomSteps(delta, false));
            }
            return true;
        }

        if (paintedSkin is not { } skin)
            return false;

        // Over the list of completions the wheel goes down the list and leaves what's picked as it is, the way VS Code's
        // does. It scrolled the code under the list
        if (CompletionOpen && Module is { } module && CompletionBounds().Contains(module.ToLocal(module.Compositor.Pointer.Position)))
        {
            float maxTop = Math.Max(0, completions.Count - COMPLETION_ROWS);
            if (maxTop <= 0.0f)
                return !UIWheel.Gliding;

            if (PreciseWheel)
            {
                float rowHeight = CompletionRow(skin);
                float before = completionShown;
                float next = Math.Clamp(before - delta * UIWheel.NotchUnits / MathF.Max(1.0f, rowHeight), 0.0f, maxTop);
                completionTop = completionShown = next;
                return !UIWheel.Gliding || next != before;
            }

            int rows = Math.Max(1, (int)MathF.Round(MathF.Abs(delta) * WHEEL_LINES));
            int sign = Math.Sign(delta);
            BeginScrollMotion(completionShown != completionTop);
            completionTop = Math.Clamp(MathF.Round(completionTop) - rows * sign, 0, maxTop);
            return true;
        }

        // As tall as its code, the wheel is for whatever it's in
        if (AllInSight)
            return false;

        // Where it's going, from where it was going if it hadn't got there yet, so a quick turn of the wheel adds up.
        // Whatever of it goes past the top or the bottom stretches the code out past it and springs back
        // A trackpad's turn moves the code by exactly what it says, the pixel, with nothing easing after it. Only
        // what's left past an end is thrown into the stretch, as it is for the wheel
        if (PreciseWheel)
        {
            float most = MaxScroll(skin, paintedScale);
            if (most <= 0.0f)
                return false;

            scrollGoal = null;
            float extent = TextArea(skin, paintedScale).Height;
            float move = -delta * UIWheel.NotchUnits;
            over.Scroll(ref scrollY, move, most, extent, MotionSpeed);
            if (UIWheel.Tracing)
                UIWheel.Trace(FormattableString.Invariant($"editor move={move:F2} y={scrollY:F2} most={most:F1} stretch={over.Stretch:F2} velocity={over.Velocity:F1}"));
            return true;
        }

        BeginScrollMotion(scrollGoal is not null || over.Active);
        float wanted = (scrollGoal ?? scrollY) - delta * WHEEL_LINES * FontOf(skin).LineHeight * paintedScale;
        scrollGoal = wanted;
        ClampScroll(skin, paintedScale);
        over.Push(wanted - (scrollGoal ?? scrollY), MotionSpeed);
        return true;
    }

    // How far across the longest visible row goes past the text area (0 when word-wrapped or empty)
    private float MaxScrollX(UISkin skin, float scale)
    {
        if (wordWrap || rows.Count == 0)
            return 0.0f;

        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        int first = Math.Clamp((int)(scrollY / MathF.Max(1.0f, lineHeight)), 0, rows.Count - 1);
        int last = Math.Min(rows.Count - 1, first + (int)(area.Height / MathF.Max(1.0f, lineHeight)) + 1);
        float widest = 0.0f;
        for (int i = first; i <= last; i++)
        {
            int end = Math.Min(rows[i].End, document.LineAt(rows[i].Line).Length);
            widest = MathF.Max(widest, ColumnX(skin, scale, rows[i].Line, end));
        }

        return MathF.Max(0.0f, widest + TEXT_PAD * 2.0f - area.Width);
    }

    // Sideways, a trackpad's or a horizontal wheel's, to the pixel. Not with the lines wrapped, there's nothing past the
    // edge then, and not past the end of the longest of the rows in sight
    protected internal override bool OnScrollX(float delta)
    {
        if (wordWrap || paintedSkin is not { } skin || rows.Count == 0)
            return false;

        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        float most = MaxScrollX(skin, paintedScale);
        float by = UIWheel.Units(delta, WHEEL_LINES * lineHeight);
        float target = Math.Clamp(scrollX - by, 0.0f, most);
        if (target == scrollX)
            return false;

        scrollX = target;
        return true;
    }

    /* Painting */

    protected override void Paint(UIDrawList list)
    {
        // Zooming, the letters come out of their field all alike until the zoom rests, see UIDrawList.FieldText
        bool field = list.FieldText;
        list.FieldText = field || Zooming;
        list.PushFont(FontOf(list.Skin));
        PaintCode(list);
        list.PopFont();
        list.FieldText = field;
    }

    private void PaintCode(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UISyntaxColors colors = CodeLanguages.ColorsFor(Highlighter, skin.Palette);
        paintedSkin = skin;

        float scale = ScaleOf(skin);
        paintedScale = scale;
        EnsureRows(skin, scale);
        ApplyZoomAnchor(skin, scale);

        // A tick the zoom changed the scroll by keeping its place: the scroll jumped, it didn't move, and the frames between
        // this tick and the last slid the new rows over from the old scroll, the code jumping about for a frame as it zoomed
        bool zoomed = scale != scaleDrawn;
        scaleDrawn = scale;
        bool jumpedBefore = list.JumpsFrames;
        list.JumpsFrames = jumpedBefore || zoomed;
        try
        {
            PaintCodeLaidOut(list, skin, colors, scale, zoomed);
        }
        finally
        {
            list.JumpsFrames = jumpedBefore;
        }
    }

    private void PaintCodeLaidOut(UIDrawList list, UISkin skin, UISyntaxColors colors, float scale, bool zoomed)
    {

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

        // And a few either side of them painted too, out of sight under the clip, so a row scrolled into sight was there
        // the tick before to be blended with. With nothing to blend with it was drawn where the newer tick had it while
        // the row next to it was still on its way, and for a frame the two sat on top of each other
        int beyond = over.Stretch == 0.0f ? 0 : (int)(MathF.Abs(over.Stretch) / lineHeight) + 1;
        int paintFrom = Math.Max(0, first - ROWS_AROUND - beyond), paintTo = Math.Min(rows.Count - 1, last + ROWS_AROUND + beyond);

        // The caret is the sign that it has the keyboard, lit or between blinks
        bool focused = IsFocused;
        if (!focused && compositionText.Length > 0)
        {
            compositionText = string.Empty;
            compositionCursor = 0;
        }
        if (focused)
            ShowingFocus();
        UITextPosition start = document.SelectionStart, end = document.SelectionEnd;
        Vector4 selection = skin.Palette.Selection;
        Vector4 band = skin.HoverColor with { W = skin.HoverColor.W * 0.35f };

        // An editor grown to its code has no rows above or below to hide, and clipped at its padding the tall letters of
        // its first and last rows (p, d, brackets) were cut a little differently every tick it moved. A cut letter can't
        // be blended with the last tick's, so while a notebook scrolled they jumped ahead of the rest of their line
        list.PushClip(AllInSight ? new UIRect(new Vector2(area.Min.X, area.Min.Y - lineHeight), new Vector2(area.Max.X, area.Max.Y + lineHeight)) : area);

        // The code moves by the scroll as one, on whole pixels (see UIDrawList.PushFrame), and a frame between two ticks
        // moves it smoothly while what's in it is the newer tick's. Only the code, the box, the bar and the minimap stay
        // put. Moved by fractions of a pixel the boxes behind the words slid while the words snapped to the pixels
        bool stretched = over.Begin(list, area);
        list.PushFrame(new Vector2(-scrollX, scrollY), jumped: zoomed);

        // What is behind the code, row by row. The caret's line, the selections, what is found, the brackets
        var extras = document.ExtraCarets;
        for (int index = paintFrom; index <= paintTo; index++)
        {
            CodeRow row = rows[index];
            float top = RowTop(area, index, lineHeight);
            if (row.Virtual)
                continue;
            list.BeginPart(RowPart(ROW_BEHIND, row));

            if (CaretBand && focused && !document.HasSelection && !document.HasExtraCarets && row.Line == document.Caret.Line)
                list.Rect(new UIRect(new Vector2(area.Min.X, top - lineHeight), new Vector2(area.Max.X, top)), band);

            if (highlightedLines.Count > 0 && highlightedLines.TryGetValue(row.Line, out Vector4 highlight))
                list.Rect(new UIRect(new Vector2(area.Min.X, top - lineHeight), new Vector2(area.Max.X, top)), highlight);

            if (row.Line == ExecutionLine)
                list.Rect(new UIRect(new Vector2(area.Min.X, top - lineHeight), new Vector2(area.Max.X, top)), skin.WarningColor with { W = 0.2f });

            // The selection in a part apart from the line's band, a selection taken away puts the band back and the two were
            // one quad, the selection stretched out across the line into it
            PaintRemoteSelections(list, skin, scale, left, top, lineHeight, digit, row);
            list.BeginPart(RowPart(ROW_SELECTED, row));
            if (document.HasSelection)
                PaintSelected(list, skin, scale, left, top, lineHeight, digit, row, start, end, selection);

            // By index, a foreach over the interface would make an enumerator every frame
            for (int i = 0; i < extras.Count; i++)
            {
                if (!extras[i].IsEmpty)
                    PaintSelected(list, skin, scale, left, top, lineHeight, digit, row, extras[i].Start, extras[i].End, selection);
            }
        }

        list.BeginPart(PART_OVERLAY);
        PaintRanges(list, skin, scale, area, left, lineHeight, firstLine, lastLine);
        if (prediction is not null)
            PaintPredictionStruck(list, skin, scale, area, left, lineHeight, digit, firstLine, lastLine);
        PaintTodos(list, skin, scale, area, left, lineHeight, firstLine, lastLine);

        if (findQuery.Length > 0 && found.Count > 0)
        {
            // The one selected stronger, it's the one enter goes on from. The rest were a fifth of the highlight and could
            // hardly be told from the code round them on the plum, so each has a faint edge of it as well
            Vector4 lit = skin.HighlightColor with { W = 0.26f };
            Vector4 edge = skin.HighlightColor with { W = 0.55f };
            Vector4 current = skin.HighlightColor with { W = 0.5f };
            for (int i = FirstFoundAtOrAfter(firstLine); i < found.Count; i++)
            {
                UITextMatch at = found[i];
                if (at.Line > lastLine) break;

                bool selected = start.Line == at.Line && start.Column == at.Column && end.Line == at.Line && end.Column == at.Column + at.Length;
                UIRect box = Piece(skin, scale, area, left, lineHeight, at.Line, at.Column, at.Column + at.Length);
                list.Rect(box, selected ? current : lit);
                list.Outline(box, 1.0f, selected ? skin.HighlightColor : edge);
            }
        }

        for (int i = 0; i < nativeOccurrences.Count; i++)
        {
            UICodeOccurrence occurrence = nativeOccurrences[i];
            if (occurrence.Line < firstLine || occurrence.Line > lastLine || occurrence.Line >= document.LineCount || occurrence.Length <= 0) continue;
            Vector4 colour = occurrence.Kind == 3 ? skin.HighlightColor with { W = 0.25f }
                : occurrence.Kind == 2 ? skin.HighlightColor with { W = 0.12f } : skin.TextColor with { W = 0.1f };
            list.Rect(Piece(skin, scale, area, left, lineHeight, occurrence.Line, occurrence.Column, occurrence.Column + occurrence.Length), colour);
        }
        if (occurrenceLength > 0 && occurrences.Count > 0)
        {
            Vector4 same = skin.TextColor with { W = 0.1f };
            for (int i = FirstOccurrenceAtOrAfter(firstLine); i < occurrences.Count; i++)
            {
                UITextPosition at = occurrences[i];
                if (at.Line > lastLine) break;
                list.Rect(Piece(skin, scale, area, left, lineHeight, at.Line, at.Column, at.Column + occurrenceLength), same);
            }
        }

        if (IndentGuides)
            PaintGuides(list, skin, scale, area, left, lineHeight, paintFrom, paintTo);
        list.BeginPart(PART_RULERS);
        PaintRulers(list, skin, area, left, digit);
        PaintWhitespace(list, skin, scale, left, area, lineHeight, paintFrom, paintTo);

        if (focused && bracket is { } one && bracketPair is { } two)
        {
            // The one further up first, whichever the caret's at. Caret's first, the two swapped over as the caret went from
            // one end to the other and each box flew across the line to where the other had been
            list.BeginPart(PART_BRACKETS);
            var (earlier, later) = one < two ? (one, two) : (two, one);
            PaintBracket(list, skin, scale, area, left, lineHeight, earlier, firstLine, lastLine);
            PaintBracket(list, skin, scale, area, left, lineHeight, later, firstLine, lastLine);
        }

        // The code itself, a row at a time, each a part of its own so a row scrolled along is blended with itself. By what
        // it is (its line and where in it it starts), not by its number, which is somebody else's row once the code is
        // wrapped again (round the picture as it scrolls by, a word typed further up), and every letter of it blended
        // with the same letter of that row wherever it was, the text a jumble of flying letters for a frame
        float origin = area.Max.Y + scrollY;
        BeginFlow(lineHeight);

        // How far either side of the code in sight a row is drawn, a width of it, room for a scroll easing between two
        // ticks. A row is drawn up to its right and only measured to its left, an unwrapped line of a fifth of a megabyte
        // (an agent's chat saved as JSON) was drawn whole every paint, every letter clipped, 50 ms a frame
        float drawnFrom = area.Min.X - area.Width, drawnTo = area.Max.X + area.Width;

        // How far into each long line the rows painted go, what reading it goes on to (LongColours). Unwrapped, as far as
        // the right edge is in digits along it
        longInSight.Clear();
        longReading = false;
        for (int index = paintFrom; index <= paintTo; index++)
        {
            CodeRow row = rows[index];
            if (row.Virtual || document.LineAt(row.Line) is not { Length: > TextMateHighlighter.LONG_LINE } longLine)
                continue;

            int to = wordWrap ? row.End : row.Start + (int)((scrollX + area.Width) / MathF.Max(1.0f, digit)) + 1;
            longInSight[row.Line] = Math.Max(longInSight.GetValueOrDefault(row.Line), Math.Min(to, longLine.Length));
        }
        float spaceWidth = FontOf(skin).Measure(" ", scale, markup: false).X;
        bool simpleGaps = !wordWrap && inlays.Count == 0 && predictedPieces.Length == 0 && !PredictionMarked;
        for (int index = paintFrom; index <= paintTo; index++)
        {
            CodeRow row = rows[index];
            if (row.Virtual)
            {
                // A line of a prediction on a row of its own, the lens rows are painted after
                if (row.Predicted > 0)
                    PaintPredictedRow(list, skin, scale, row, RowTop(area, index, lineHeight), left, colors);
                continue;
            }
            list.BeginPart(HashCode.Combine(row.Line, row.Start) & 0x3FFFFFFF);
            float top = RowTop(area, index, lineHeight);
            string written = document.LineAt(row.Line);
            float x = left + row.Indent;

            // A row cut off by the bottom edge fades out there rather than being sliced in half, the way a scrolled list
            // does (UIDrawList.PushFade). Only the row that's cut, the letters hanging low in a row all in sight stay whole
            bool cut = top - lineHeight < area.Min.Y - 0.5f;
            if (cut)
                list.PushFade(area, 0.0f, lineHeight * BOTTOM_FADE);

            int rowEnd = Math.Min(row.End, written.Length);

            // Typed into and not coloured with the rest yet, coloured on its own, or all one colour for a moment when it
            // can't be (it starts inside a comment over several lines)
            if (DrawnColours(row.Line, written) is not { } colours)
            {
                x = DrawInSight(list, skin, scale, row.Line, row.Start, written.AsSpan(row.Start, rowEnd - row.Start), x, top, colors.Of(SyntaxKind.Name), left, origin, colors, drawnFrom, drawnTo);
            }
            else
            {
                int at = row.Start;
                foreach (SyntaxSpan span in colours)
                {
                    if (x > drawnTo || span.Start >= rowEnd)
                        break;
                    if (span.Start < 0 || span.Length <= 0)
                        continue;

                    // Only what of the span is on this row, clamped to [at..rowEnd] so stale or overlapping spans never index out of bounds
                    int from = Math.Max(Math.Max(span.Start, row.Start), at);
                    int to = Math.Min(span.Start + span.Length, rowEnd);
                    if (to <= from)
                        continue;

                    // The gap before it is spaces in code a grammar coloured, stepped over. Text a terminal coloured
                    // has words in its gaps, the plain ones between two coloured, and a traceback's code went missing
                    // round its keywords
                    if (from > at)
                    {
                        if (simpleGaps && from - at == 1 && written[at] == ' ')
                        {
                            x += spaceWidth;
                        }
                        else
                        {
                            ReadOnlySpan<char> gap = written.AsSpan(at, from - at);
                            bool blank = gap.IsWhiteSpace();
                            x = blank ? DrawWithInlays(list, skin, scale, row.Line, at, gap, x, top, default, left, origin, colors, draw: false)
                                : DrawInSight(list, skin, scale, row.Line, at, gap, x, top, colors.Of(SyntaxKind.Name), left, origin, colors, drawnFrom, drawnTo);
                        }
                    }
                    Vector4 colour = ColourOf(colors, span);
                    float started = x;
                    x = DrawInSight(list, skin, scale, row.Line, from, written.AsSpan(from, to - from), x, top, colour, left, origin, colors, drawnFrom, drawnTo);
                    if (span.Modifiers != SyntaxModifiers.None)
                        DrawModifiers(list, span.Modifiers, started, x, top, lineHeight, StrikeY(skin, scale, top, lineHeight), colour);
                    at = to;
                }

                if (at < rowEnd && x <= drawnTo)
                    x = DrawInSight(list, skin, scale, row.Line, at, written.AsSpan(at, rowEnd - at), x, top, colors.Of(SyntaxKind.Name), left, origin, colors, drawnFrom, drawnTo);
            }

            // What a prediction puts after the end of the line, then the hints, once its last row is drawn
            if (rowEnd == written.Length && prediction is not null)
                x = DrawPredictedAtEnd(list, skin, scale, row.Line, written.Length, x, top, colors);
            if (rowEnd == written.Length)
                x = DrawInlaysAtEnd(list, skin, scale, row.Line, written.Length, x, top, colors);

            // A folded line says what it folds away after it, the closing line, the way VS Code does
            if (rowEnd == written.Length && folded.Contains(row.Line) && foldable.TryGetValue(row.Line, out int foldEnd))
                PaintFolded(list, skin, scale, x, top, lineHeight, foldEnd, colors);

            if (cut)
                list.PopFade();
        }

        // The lenses over the lines, on the rows left for them
        PaintLensesAbove(list, skin, scale, area, left, lineHeight, first, last, colors);

        // The colours written in the code, a swatch at the end of their line
        list.BeginPart(PART_SWATCHES);
        if (swatches.Count > 0)
        {
            for (int i = FirstSwatchAtOrAfter(firstLine); i < swatches.Count; i++)
            {
                HidlSwatch swatch = swatches[i];
                if (swatch.Line > lastLine) break;
                if (swatch.Line < 0 || swatch.Line >= document.LineCount || IsHidden(swatch.Line, out _)) continue;

                UIRect box = SwatchBounds(skin, scale, area, left, lineHeight, i);
                list.Rect(box.Shrink(new UIEdges(-1.0f)), skin.TextColor with { W = 0.5f });
                list.Rect(box, swatch.Color with { W = 1.0f });
            }
        }

        // The art named in the code, after the swatches
        PaintArt(list, skin, scale, area, left, lineHeight, firstLine, lastLine);

        // What the program has to say after the end of a line, the value a let came to, in the same pill as the hints in
        // among the code. It was plain grey words two spaces on, and the names in the middle of the line were in pills
        if (inlineHintsSorted.Count > 0)
        {
            for (int i = FirstInlineHintAtOrAfter(firstLine); i < inlineHintsSorted.Count; i++)
            {
                UICodeHint hint = inlineHintsSorted[i];
                if (hint.Line > lastLine) break;
                if (hint.Line < 0 || hint.Line >= document.LineCount || IsHidden(hint.Line, out _))
                    continue;

                int length = document.LineAt(hint.Line).Length;
                float x = left + EndX(skin, scale, hint.Line, length) + digit * (SwatchesOn(hint.Line) * 2 + INLAY_END_GAP) + ArtWidthOn(hint.Line);
                DrawPill(list, skin, scale, hint.Text, x, TopOf(area, hint.Line, length, lineHeight), colors);
            }
        }

        PaintLenses(list, skin, scale, area, left, lineHeight, digit, firstLine, lastLine);

        // The caret line's note, after its hint if it has one, fainter, and only while the caret is on its line. Well clear
        // of the code, a bracket lit at the end of the line and the note read as one thing
        float noteShown = Math.Clamp((lineNoteAge - LineNoteDelay) / LINE_NOTE_FADE, 0.0f, 1.0f);
        // Not while a prediction shows, its ghost text and the note would be drawn over each other
        if (noteShown > 0.0f && prediction is null && LineNote is { } note && note.Line == lineNoteFor && note.Line == document.Caret.Line && note.Line >= firstLine && note.Line <= lastLine && note.Line < document.LineCount && !IsHidden(note.Line, out _))
        {
            int length = document.LineAt(note.Line).Length;
            float x = left + EndX(skin, scale, note.Line, length) + digit * (SwatchesOn(note.Line) * 2 + LINE_NOTE_GAP) + ArtWidthOn(note.Line) + LensWidthOn(note.Line, digit);
            for (int i = FirstInlineHintAtOrAfter(note.Line); i < inlineHintsSorted.Count && inlineHintsSorted[i].Line == note.Line; i++)
                x += EndHintWidth(skin, scale, inlineHintsSorted[i].Text, digit);

            list.Text(note.Text, new Vector2(x, TopOf(area, note.Line, length, lineHeight)), scale, colors.Comment with { W = colors.Comment.W * LINE_NOTE_INK * noteShown }, markup: false);
        }

        // What is wrong, a line under it
        if (SyntaxError is { } error)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, error, firstLine, lastLine);
        for (int i = 0; i < moreErrors.Count; i++)
            PaintDiagnostic(list, skin, scale, area, left, lineHeight, moreErrors[i], firstLine, lastLine);
        if (diagnosticsSorted.Length > 0)
        {
            for (int i = FirstDiagnosticAtOrAfter(firstLine); i < diagnosticsSorted.Length; i++)
            {
                if (diagnosticsSorted[i].Line > lastLine) break;
                PaintDiagnostic(list, skin, scale, area, left, lineHeight, diagnosticsSorted[i], firstLine, lastLine);
            }
        }

        PaintSpelling(list, skin, scale, area, left, lineHeight, firstLine, lastLine);
        PaintAgentHints(list, skin, scale, area, left, lineHeight, digit, firstLine, lastLine);
        PaintJumpMarkers(list, skin, scale, area, left, lineHeight, digit, firstLine, lastLine);

        // How the call being typed is called, over it
        if (focused && ShownSignature() is { } said)
            PaintSignature(list, skin, scale, area, left, lineHeight, first, said);

        PaintRemoteCarets(list, skin, scale, area, left, lineHeight, first, last);

        // Inline IME preedit composition, shown at the primary caret even between caret blinks
        if (focused && compositionText.Length > 0)
        {
            list.BeginPart(PART_CARET);
            PaintComposition(list, skin, scale, area, left, lineHeight, first, last, document.Caret);
        }

        // The caret, which blinks, and stays lit while it is moved or typed with, and every other caret there is
        if (focused && blink < BLINK)
        {
            list.BeginPart(PART_CARET);
            PaintCaret(list, skin, scale, area, left, lineHeight, first, last, document.Caret);
            for (int i = 0; i < extras.Count; i++)
                PaintCaret(list, skin, scale, area, left, lineHeight, first, last, extras[i].Caret);
        }

        list.PopFrame();
        if (stretched)
            UIOverscroll.End(list);
        list.PopClip();

        // The numbers down the side, the caret's lit, a mark where something is wrong, and a chevron for what folds. They
        // go up and down with the code and not across with it
        if (LineNumbers)
        {
            bool gutterStretched = over.Begin(list, new UIRect(new Vector2(content.Min.X, area.Min.Y), area.Max));
            list.PushFrame(new Vector2(0.0f, scrollY), jumped: zoomed);
            // The gutter is the look's and not the language's, C# in Visual Studio's colours had teal numbers on Solstice
            // Day's paper and a Markdown file beside it grey ones
            PaintGutter(list, skin, scale, content, area, lineHeight, digit, paintFrom, paintTo, origin, skin.Palette.Syntax);
            list.PopFrame();
            if (gutterStretched)
                UIOverscroll.End(list);
        }

        EndFlow();

        // The lines the top of the view is inside of, held there over the code and its numbers, a click on one going to it
        list.PushClip(new UIRect(content.Min, new Vector2(area.Max.X, content.Max.Y)));
        PaintSticky(list, skin, scale, content, area, lineHeight, digit, firstLine, colors);
        list.PopClip();

        // The bar, as long against its track as what is in sight is against all of it, and the line along the top that
        // says it has the keyboard, a hairline of the accent and not a stripe of it. The thumb is the text seen through,
        // the way ScrollPanel's is, it was a bar of gold down the side of every file
        PaintMinimap(list, skin, scale, colors, lineHeight);

        list.BeginPart(PART_BAR);
        if (FocusLine && focused)
            list.Rect(new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - 1.0f), Bounds.Max), skin.AccentColor with { W = 0.55f });
        PaintBar(list, skin, lineHeight, area);
        PaintMiddleScroll(list);

        // The art under the pointer, big, over everything
        if (hoveredArt >= 0 && hoveredArt < art.Count && hoveredArt < artBoxes.Length && artBoxes[hoveredArt].Width > 0.0f)
            PaintArtLarge(list, skin, lineHeight, art[hoveredArt], artBoxes[hoveredArt]);

        list.EndParts();
    }

    // The box a piece of a line takes from one column to another, bounded to the wrapped row that `from` is on.
    private UIRect Piece(UISkin skin, float scale, UIRect area, float left, float lineHeight, int line, int from, int to)
    {
        if (document.LineCount == 0)
            return default;

        line = Math.Clamp(line, 0, document.LineCount - 1);
        string text = document.LineAt(line);
        from = Math.Clamp(from, 0, text.Length);
        to = Math.Clamp(to, from, text.Length);

        int rowIndex = RowOf(line, from);
        float top = RowTop(area, rowIndex, lineHeight);
        if (rows.Count == 0 || rows[rowIndex].Line != line)
        {
            float fallbackX0 = ColumnX(skin, scale, line, from);
            float fallbackX1 = ColumnX(skin, scale, line, to);
            return new UIRect(new Vector2(left + fallbackX0, top - lineHeight), new Vector2(left + fallbackX1, top));
        }

        CodeRow row = rows[rowIndex];
        int rowStart = Math.Min(row.Start, from);
        int endOnRow = Math.Min(to, Math.Min(row.End, text.Length));
        float x0 = row.Indent + (from > rowStart ? Width(skin, scale, text.AsSpan(rowStart, from - rowStart)) + InlaysWidth(skin, scale, line, rowStart, from) : 0.0f);
        float x1 = endOnRow > from ? x0 + Width(skin, scale, text.AsSpan(from, endOnRow - from)) + InlaysWidth(skin, scale, line, from, endOnRow) : x0;
        return new UIRect(new Vector2(left + x0, top - lineHeight), new Vector2(left + x1, top));
    }

    /// <summary>The painted rectangle for a column span on its starting visual row, for tests.</summary>
    internal UIRect PieceBounds(int line, int from, int to)
    {
        if (paintedSkin is not { } skin)
            return default;
        float scale = paintedScale;
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        EnsureRows(skin, scale);
        return Piece(skin, scale, area, area.Min.X + TEXT_PAD - scrollX, lineHeight, line, from, to);
    }

    /* Signature help, how the function being called at the caret is called */

    private UISignature? signature;
    private UITextPosition signatureAt;
    private (int Version, UITextPosition Caret) signatureFor = (-1, default);

    // How many overloads on from the one the source picked up and down went, and whether escape took the box away, both
    // for the call they were for and forgotten in another
    private int overloadStep;
    private bool signatureHidden;
    private (UITextPosition At, string Name) signatureOf;

    private static int Wrap(int index, int count) => ((index % count) + count) % count;

    // The overload up and down went to, nothing while escape has it away
    private UISignature? ShownSignature()
    {
        if (signatureHidden || signature is not { } said)
            return null;
        return said.Overloads is { Count: > 1 } all ? all[Wrap(said.Active + overloadStep, all.Count)] with { Overloads = all, Active = said.Active } : said;
    }

    /// <summary>
    /// The box over the call at the caret taken away the way Escape takes it, for another popup coming up over the code
    /// (the fixes of Ctrl+., the hover). It stayed up over them, saying what a call the caret had left took.
    /// </summary>
    public void HideSignature() => signatureHidden = true;

    /// <summary>The box over the call at the caret said again after escape took it away (Ctrl+Shift+Space), and asked for afresh.</summary>
    public void ShowSignature()
    {
        signatureHidden = false;
        signatureFor = (-1, default);
    }

    // Up or down with a call of more than one overload said, the next one or the one before it. False for anything else,
    // the caret goes up or down then
    private bool StepOverload(int by)
    {
        if (ShownSignature() is not { Overloads.Count: > 1 })
            return false;

        overloadStep += by;
        return true;
    }

    // The call the caret's in found, and out of every call the next one is said afresh. Escape's was for the call it
    // was pressed in, and going back into that one later had nothing said of it
    private void FindSignature()
    {
        if (FindCall())
            return;

        signatureOf = default;
        overloadStep = 0;
        signatureHidden = false;
    }

    // Back from the caret to the bracket of the call it's inside of, counting the commas on the way, and the name before
    // it. False when it's in none
    private bool FindCall()
    {
        signatureFor = (document.Version, document.Caret);
        signature = null;
        if (SignatureSource is not { } source || document.HasSelection)
            return false;

        int line = document.Caret.Line, column = document.Caret.Column, depth = 0, commas = 0;
        for (int steps = 0; steps < 4000; steps++)
        {
            if (column == 0)
            {
                if (--line < 0)
                    return false;
                column = document.LineAt(line).Length;
                continue;
            }

            string text = document.LineAt(line);
            char at = text[--column];
            if (at == '\'' && column > 0 && column + 1 < text.Length
                && char.IsAsciiHexDigit(text[column - 1]) && char.IsAsciiHexDigit(text[column + 1]))
            {
                // C++ digit separators are not character-literal delimiters.
                continue;
            }
            if (at is '"' or '\'')
            {
                // A text is gone over whole, its commas and brackets aren't the call's
                int open = column - 1;
                for (; open >= 0; open--)
                {
                    if (text[open] != at) continue;
                    int slashes = 0;
                    for (int before = open - 1; before >= 0 && text[before] == '\\'; before--) slashes++;
                    if (slashes % 2 == 0) break;
                }
                if (open < 0)
                    return false;
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
                    return false;

                int end = column;
                while (end > 0 && text[end - 1] == ' ') end--;
                int start = end;
                while (start > 0 && (UITextDocument.IsWordChar(text[start - 1]) || text[start - 1] == '.')) start--;
                if (end <= start)
                    return false;

                string name = text[start..end];
                signature = source(name, commas);
                signatureAt = new UITextPosition(line, start);

                // Another call starts over at the overload the source picks, and is said again after escape
                if (signatureOf != (signatureAt, name))
                {
                    signatureOf = (signatureAt, name);
                    overloadStep = 0;
                    signatureHidden = false;
                }
                return true;
            }
            else if (at == ',' && depth == 0)
            {
                commas++;
            }
            else if (at == ';' && depth == 0)
            {
                return false;
            }
        }

        return false;
    }

    // The part of a selection on a row, to the end of the row and a little past it on the rows it runs over
    private void PaintSelected(UIDrawList list, UISkin skin, float scale, float left, float top, float lineHeight, float digit, CodeRow row, UITextPosition start, UITextPosition end, Vector4 color)
    {
        if (end.Line < row.Line || start.Line > row.Line)
            return;

        string text = document.LineAt(row.Line);
        int rowEndColumn = Math.Min(row.End, text.Length);
        var rowStart = new UITextPosition(row.Line, row.Start);
        var rowEnd = new UITextPosition(row.Line, rowEndColumn);
        if (end < rowStart || start > rowEnd || (end == rowStart && row.Start > 0))
            return;

        int fromColumn = start > rowStart ? start.Column : row.Start;
        int toColumn = end < rowEnd ? end.Column : rowEndColumn;

        // Starting at a character that has a hint before it, the hint isn't in it, and a hint before where it ends isn't either
        float from = row.Indent + Width(skin, scale, text.AsSpan(row.Start, Math.Max(0, fromColumn - row.Start))) + InlaysWidth(skin, scale, row.Line, row.Start, start > rowStart ? fromColumn + 1 : fromColumn);
        float to = row.Indent + Width(skin, scale, text.AsSpan(row.Start, Math.Max(0, toColumn - row.Start))) + InlaysWidth(skin, scale, row.Line, row.Start, toColumn);
        if (end > rowEnd && rowEndColumn == text.Length)
            to += InlaysWidth(skin, scale, row.Line, text.Length, int.MaxValue) + digit * 0.5f;

        if (to > from)
            list.Rect(new UIRect(new Vector2(left + from, top - lineHeight), new Vector2(left + to, top)), color);
    }

    /// <summary>
    /// How long the caret takes to glide to where it went, in seconds, all the way there in that long and quickest at
    /// the start, 0 for it to jump there the way it always did. Only the caret that is drawn glides, typing goes where
    /// the caret is.
    /// </summary>
    public float CaretGlide { get; set; }

    /// <summary>How the caret is drawn, a thin line before the character, a block over it or a line under it.</summary>
    public UICaretStyle CaretStyle { get; set; }

    /// <summary>Where the caret was drawn last, the middle of its left edge, for something to happen there (sparks).</summary>
    public Vector2 CaretPoint { get; private set; }

    /// <summary>How tall the caret's line was drawn last, for something put under it (Visual Studio's fixes by the caret).</summary>
    public float CaretLineHeight { get; private set; }

    // How long the last tick was, for the glide, and where the caret is drawn against the code (from the top of its first
    // row, so scrolling moves it with the code rather than it gliding after it)
    private float tickTime;
    private Vector2 caretDrawn;
    private bool caretSeen;

    // The glide going, from where the caret was drawn to where it went and how far into it. A glide that eased towards
    // the caret forever crept up on it over its last few pixels, which read as a caret slower than every other editor's
    private Vector2 glideFrom, glideTo;
    private float glideTime;

    private void PaintComposition(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, int last, UITextPosition caret)
    {
        int row = RowOf(caret.Line, caret.Column);
        if (row < first || row > last || IsHidden(caret.Line, out _))
            return;

        float top = RowTop(area, row, lineHeight);
        float x = left + ColumnX(skin, scale, caret.Line, caret.Column);
        float compWidth = FontOf(skin).Measure(compositionText, scale, markup: false).X;
        float boxWidth = MathF.Max(compWidth, 4.0f);

        // Cover existing characters behind the inline preedit text so marked text reads cleanly
        list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + boxWidth, top)), Color ?? skin.FieldColor);
        list.Text(compositionText, new Vector2(x, top), scale, skin.TextColor, markup: false);
        list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + boxWidth, top - lineHeight + 1.5f)), skin.HighlightColor);
        ReportTextInputRect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + boxWidth, top)));
    }

    private void PaintCaret(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, int last, UITextPosition caret)
    {
        int row = RowOf(caret.Line, caret.Column);
        if (row < first || row > last || IsHidden(caret.Line, out _))
            return;

        float top = RowTop(area, row, lineHeight);
        float x = left + ColumnX(skin, scale, caret.Line, caret.Column);
        float placedX = x, placedTop = top;
        if (caret == document.Caret)
        {
            Vector2 origin = new(left, RowTop(area, 0, lineHeight));
            Vector2 wanted = new Vector2(x, top) - origin;

            // A long way (a page, the end of the file) is jumped, a glide over half the screen only looks slow
            if (CaretGlide <= 0.0f || !caretSeen || Vector2.Distance(caretDrawn, wanted) > lineHeight * 12.0f)
            {
                caretDrawn = glideFrom = glideTo = wanted;
                glideTime = CaretGlide;
            }
            else
            {
                if (wanted != glideTo)
                {
                    glideFrom = caretDrawn;
                    glideTo = wanted;
                    glideTime = 0.0f;
                }

                glideTime = MathF.Min(glideTime + tickTime, CaretGlide);
                float t = 1.0f - glideTime / CaretGlide;
                caretDrawn = Vector2.Lerp(glideFrom, glideTo, 1.0f - t * t * t);
            }

            caretSeen = true;
            x = origin.X + caretDrawn.X;
            top = origin.Y + caretDrawn.Y;
            CaretPoint = new Vector2(x, top - lineHeight * 0.5f);
            CaretLineHeight = lineHeight;
            ReportTextInputRect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + 2.0f, top)));
        }

        if (CaretStyle == UICaretStyle.Line)
        {
            list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + MathF.Max(1.0f, MathF.Round(scale * 8.0f)), top)), skin.HighlightColor);
            return;
        }

        // A block or an underline is as wide as the character it's on, a tab's room for a tab and a digit at the end
        string text = document.LineAt(caret.Line);
        int column = Math.Clamp(caret.Column, 0, text.Length);
        float width = column < text.Length && RowOf(caret.Line, column + 1) == row
            ? ColumnX(skin, scale, caret.Line, column + 1) - ColumnX(skin, scale, caret.Line, column)
            : FontOf(skin).Measure("0", scale, markup: false).X;
        width = MathF.Max(width, 1.0f);

        if (CaretStyle == UICaretStyle.Underline)
        {
            float thick = MathF.Max(1.0f, MathF.Round(scale * 8.0f));
            list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + width, top - lineHeight + thick)), skin.HighlightColor);
            return;
        }

        // The block, and the character under it written again on top in the colour behind the code so it still reads.
        // Where the character really is, the block glides over it
        list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + width, top)), skin.HighlightColor);
        if (column < text.Length && text[column] is not ('\t' or ' '))
        {
            Vector4 behind = Color ?? skin.FieldColor;
            behind = behind.W > 0.0f ? behind with { W = 1.0f } : new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
            list.Text(text.AsSpan(column, char.IsHighSurrogate(text[column]) && column + 1 < text.Length ? 2 : 1), new Vector2(placedX, placedTop), scale, behind, markup: false);
        }
    }

    // After a folded line, a box with the lines it hides counted and the line that closes it, "… }"
    private void PaintFolded(UIDrawList list, UISkin skin, float scale, float x, float top, float lineHeight, int foldEnd, UISyntaxColors colors)
    {
        ReadOnlySpan<char> closing = foldEnd >= 0 && foldEnd < document.LineCount ? document.LineAt(foldEnd).AsSpan().Trim() : default;
        float gap = FontOf(skin).Measure(" ", scale, markup: false).X;
        float dots = FontOf(skin).Measure("…", scale, markup: false).X;
        var box = new UIRect(new Vector2(x + gap, top - lineHeight + 2.0f), new Vector2(x + gap * 3.0f + dots, top - 2.0f));
        list.Rect(box, skin.HoverColor);
        list.Text("…", new Vector2(x + gap * 2.0f, top), scale, colors.Comment, markup: false);
        if (!closing.IsEmpty)
            Draw(list, skin, scale, closing, box.Max.X + gap, top, colors.Of(SyntaxKind.Punctuation));
    }

    // The lines the top of the view is inside of, on a strip of their own over the code and the gutter, numbered
    /// <summary>
    /// Helper method to remember, before the scale changes, which line a zoom keeps in place and how far down the view it
    /// is. The caret's when it's in sight, the top row's otherwise. A second zoom before the first was painted keeps the first's.
    /// </summary>
    private void KeepPlaceThroughZoom()
    {
        if (zoomAnchor is not null || paintedSkin is not { } skin || rows.Count == 0)
            return;

        // The middle of the view stays where it is, the way a pinch zooms a page in Safari. It was the caret's line or the
        // top one, so the code grew out of the line's start and the top of the view
        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        UIRect area = TextArea(skin, paintedScale);
        float middle = scrollY + area.Height * 0.5f;
        int row = Math.Clamp((int)(middle / lineHeight), 0, rows.Count - 1);
        float down = middle / lineHeight;
        while (rows[row].Virtual && row + 1 < rows.Count)
        {
            row++;
            down = row;
        }

        // How far through the whole line the middle is, its share of all the rows the line wraps onto. By the row it was
        // in, a wrapped line breaking somewhere else at the new size moved the middle a row up or down, and every line
        // shook as a pinch went on
        int line = rows[row].Line;
        LineSpan(line, out int first, out int count);
        float share = Math.Clamp((down - first) / count, 0.0f, 1.0f);
        zoomAnchor = (line, 0, share, paintedScale);
    }

    // Helper method for the rows a line is drawn on: the first and how many, its lenses' row over it left out
    private void LineSpan(int line, out int first, out int count)
    {
        first = line < lineRow.Length ? Math.Clamp(lineRow[line], 0, Math.Max(0, rows.Count - 1)) : 0;
        int end = first + 1;
        while (end < rows.Count && rows[end].Line == line && !rows[end].Lens && !rows[end].Virtual)
            end++;
        count = end - first;
    }

    /// <summary>Helper method to scroll the line a zoom kept back to where it was on the screen, once the rows are laid out at the new scale.</summary>
    private void ApplyZoomAnchor(UISkin skin, float scale)
    {
        if (zoomAnchor is not { } anchor || anchor.OldScale == scale)
        {
            zoomAnchor = null;
            return;
        }

        zoomAnchor = null;
        float lineHeight = FontOf(skin).LineHeight * scale;
        UIRect area = TextArea(skin, scale);
        scrollGoal = null;
        LineSpan(anchor.Line, out int first, out int count);
        scrollY = (first + anchor.Into * count) * lineHeight - area.Height * 0.5f;

        // Sideways the middle of the view stays put too, once the code has been scrolled along from its start. At the start
        // the start stays in sight, as the lines grow out to the right of it
        if (!wordWrap && scrollX > 0.0f)
        {
            float half = area.Width * 0.5f;
            scrollX = (scrollX + half) * (scale / MathF.Max(0.0001f, anchor.OldScale)) - half;
        }

        ClampScroll(skin, scale);
    }

    // How wide a change's bar is and how much of the gutter next to the code it takes, in units of a digit's width
    private const float CHANGE_BAR = 0.3f, CHANGE_ZONE = 0.8f;

    // A press that went to a change's bar, which a drag after it leaves alone
    private bool changePressed;

    /// <summary>
    /// Helper method to paint the bar of a changed line down all of its rows, and the wedge of lines taken out above it.
    /// The line's top is where its number goes, the first row's as it flows, and the bar is as tall as every row it
    /// wraps onto. Painted a row at a time a wrapped line's bars flowed apart from each other while the lines moved.
    /// </summary>
    private void PaintLineChange(UIDrawList list, UISkin skin, UIRect area, int index, float origin, float lineHeight, float digit, GutterColumns columns)
    {
        int line = rows[index].Line;
        int from = index, to = index;
        while (from > 0 && rows[from - 1].Line == line && !rows[from - 1].Lens && !rows[from - 1].Block) from--;
        while (to + 1 < rows.Count && rows[to + 1].Line == line && rows[to + 1].Predicted == 0 && !rows[to + 1].Block) to++;

        float top = FlowTop(line, RowTop(area, from, lineHeight), origin);
        float bottom = top - (to - from + 1) * lineHeight;
        float width = columns.BarRight - columns.BarLeft;
        float right = columns.BarRight;

        // A folded line has the bar of anything changed that's folded away under it, or nobody would know it was there
        bool hiddenChange = false, hiddenDelete = false;
        if (folded.Contains(line) && foldable.TryGetValue(line, out int foldEnd))
        {
            for (int under = line + 1; under <= foldEnd && !(hiddenChange && hiddenDelete); under++)
            {
                hiddenChange |= changedLines.ContainsKey(under);
                hiddenDelete |= deletedBefore.Contains(under);
            }
        }

        if (changedLines.TryGetValue(line, out UILineChangeKind kind) || hiddenChange)
        {
            Vector4 colour = hiddenChange ? skin.WarningColor : ChangeColour(kind, skin);
            list.Box(new UIRect(new Vector2(right - width, bottom), new Vector2(right, top)), colour);
        }

        // Lines taken out from just before this one, a wedge across the line between, out into the gutter so it doesn't
        // read as a dot. Lines taken out at the very end of a text with no line after them have theirs under the last one
        float half = MathF.Max(2.0f, MathF.Round(lineHeight * 0.16f));
        float wedge = width * 4.0f;
        if (deletedBefore.Contains(line) || hiddenDelete)
            list.Box(new UIRect(new Vector2(right - wedge, top - half), new Vector2(right, top + half)), skin.ErrorColor);
        if (line == document.LineCount - 1 && deletedBefore.Contains(document.LineCount))
            list.Box(new UIRect(new Vector2(right - wedge, bottom - half), new Vector2(right, bottom + half)), skin.ErrorColor);
    }

    /// <summary>Helper method for a press on a change's bar, handed to whoever wants it. False when it wasn't on one.</summary>
    private bool PressLineChange(Vector2 point, UITextPosition at)
    {
        if (OnChangePressed is null || paintedSkin is not { } skin || lineChanges.Count == 0)
            return false;

        float digit = FontOf(skin).Measure("0", paintedScale, markup: false).X;
        float codeStarts = TextArea(skin, paintedScale).Min.X;
        if (point.X >= codeStarts || point.X < codeStarts - digit * CHANGE_ZONE)
            return false;

        // The change the line is in, or the lines taken out just above it
        foreach (UICodeLineChange change in lineChanges)
        {
            bool on = change.Kind == UILineChangeKind.Deleted
                ? at.Line == change.Line || at.Line == change.Line - 1
                : at.Line >= change.Line && at.Line < change.Line + change.Count;
            if (on)
            {
                // The press was the bar's, a drag from it isn't a selection from the caret
                changePressed = true;
                OnChangePressed(change);
                return true;
            }
        }

        return false;
    }

    /// <summary>The lines held at the top as they were last painted, outermost first, for a test.</summary>
    public IReadOnlyList<int> StickyShown => sticky;

    private void PaintSticky(UIDrawList list, UISkin skin, float scale, UIRect content, UIRect area, float lineHeight, float digit, int firstLine, UISyntaxColors colors)
    {
        List<int> held = StickyLines(firstLine);
        if (held.Count == 0)
            return;

        list.BeginPart(PART_STICKY);
        UIRect strip = StickyBounds(area, lineHeight, held.Count);
        strip = new UIRect(new Vector2(content.Min.X, strip.Min.Y), strip.Max);
        // Solid whatever the editor's own colour is, a see-through editor (over Hex's picture) had the lines scrolled under
        // the strip showing through it. A soft shadow under it, VS Code's, lifts it off the code going by under it, a
        // hairline of the text's colour was all there was and it read as one more line of code
        Vector4 back = Color is { W: >= 0.99f } solid ? solid : skin.PanelColor with { W = 1.0f };
        float lightness = 0.2126f * back.X + 0.7152f * back.Y + 0.0722f * back.Z;
        float soft = MathF.Max(3.0f, MathF.Round(lineHeight * 0.3f));
        list.Shadow(strip, 0.0f, soft, new Vector4(0.0f, 0.0f, 0.0f, lightness > 0.5f ? 0.18f : 0.5f), new Vector2(0.0f, -soft * 0.5f));
        list.Rect(strip, back);
        list.Rect(new UIRect(strip.Min, new Vector2(strip.Max.X, strip.Min.Y + 1.0f)), skin.TextColor with { W = 0.06f });

        // Gutter line numbers stay anchored while sticky header code shifts with horizontal scrollX under the code clip.
        if (LineNumbers)
        {
            list.PushClip(new UIRect(new Vector2(content.Min.X, strip.Min.Y), new Vector2(area.Min.X, strip.Max.Y)));
            Span<char> number = stackalloc char[12];
            for (int i = 0; i < held.Count; i++)
            {
                int line = held[i];
                if ((uint)line >= (uint)document.LineCount)
                    continue;
                float top = area.Max.Y - i * lineHeight;
                Number(list, number, diffNumbers is var (_, news) ? (line < news.Length ? news[line] : 0) : line + FirstLineNumber, area.Min.X - digit * GUTTER_GAP, top, scale, digit, skin.Palette.Syntax.LineNumber);
            }
            list.PopClip();
        }

        list.PushClip(new UIRect(new Vector2(area.Min.X, strip.Min.Y), new Vector2(area.Max.X, strip.Max.Y)));
        for (int i = 0; i < held.Count; i++)
        {
            int line = held[i];
            if ((uint)line >= (uint)document.LineCount)
                continue;
            string written = document.LineAt(line);
            float top = area.Max.Y - i * lineHeight;
            float x = area.Min.X + TEXT_PAD - scrollX;
            int at = 0;

            if (DrawnColours(line, written) is { } colours)
            {
                foreach (SyntaxSpan span in colours)
                {
                    if (x > area.Max.X || span.Start >= written.Length)
                        break;
                    if (span.Start < 0 || span.Length <= 0)
                        continue;

                    int from = Math.Max(span.Start, at);
                    int to = Math.Min(span.Start + span.Length, written.Length);
                    if (to <= from)
                        continue;

                    if (from > at)
                    {
                        ReadOnlySpan<char> gap = written.AsSpan(at, from - at);
                        x = gap.IsWhiteSpace() ? x + Width(skin, scale, gap) : DrawSticky(list, skin, scale, gap, x, top, colors.Of(SyntaxKind.Name), area.Min.X, area.Max.X);
                    }

                    x = DrawSticky(list, skin, scale, written.AsSpan(from, to - from), x, top, ColourOf(colors, span), area.Min.X, area.Max.X);
                    at = to;
                }

                if (at < written.Length && x <= area.Max.X)
                    DrawSticky(list, skin, scale, written.AsSpan(at), x, top, colors.Of(SyntaxKind.Name), area.Min.X, area.Max.X);
            }
            else
            {
                DrawSticky(list, skin, scale, written, x, top, colors.Of(SyntaxKind.Name), area.Min.X, area.Max.X);
            }
        }
        list.PopClip();
    }

    // Helper method to draw a sticky-header text span while skipping glyphs completely scrolled past area.Min.X.
    private float DrawSticky(UIDrawList list, UISkin skin, float scale, ReadOnlySpan<char> text, float x, float top, Vector4 color, float minX, float maxX)
    {
        if (text.IsEmpty || x > maxX)
            return x;

        if (x >= minX)
            return Draw(list, skin, scale, text, x, top, color);

        float tab = TabWidth(skin, scale);
        UIFont font = FontOf(skin);
        int at = 0;
        while (at < text.Length)
        {
            char c = text[at];
            int len = c == '\t' ? 1 : (char.IsHighSurrogate(c) && at + 1 < text.Length ? 2 : 1);
            float w = c == '\t' ? tab : font.Measure(text.Slice(at, len), scale, markup: false).X;
            if (x + w > minX)
                break;
            x += w;
            at += len;
        }

        return at < text.Length && x <= maxX ? Draw(list, skin, scale, text[at..], x, top, color) : x;
    }

    private int FirstSwatchAtOrAfter(int line)
    {
        int low = 0, high = swatches.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (swatches[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    // How many swatches a line has at its end, for what's written after them
    private int SwatchesOn(int line)
    {
        if (swatches.Count == 0)
            return 0;

        int count = 0;
        for (int i = FirstSwatchAtOrAfter(line); i < swatches.Count && swatches[i].Line == line; i++)
            count++;
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
        float x = left + EndX(skin, scale, swatch.Line, length) + side * 0.8f + before * (side + 4.0f);
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

    private int FirstArtAtOrAfter(int line)
    {
        int low = 0, high = art.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (art[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

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

        for (int i = FirstArtAtOrAfter(firstLine); i < art.Count && i < artBoxes.Length; i++)
        {
            HidlArt item = art[i];
            if (item.Line > lastLine)
                break;
            if (item.Line < 0 || item.Line >= document.LineCount || IsHidden(item.Line, out _))
                continue;

            if (item.Line != line)
            {
                line = item.Line;
                int length = document.LineAt(line).Length;
                x = left + EndX(skin, scale, line, length) + swatchSide * 0.8f + SwatchesOn(line) * (swatchSide + ART_GAP);
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
        if (art.Count == 0)
            return 0.0f;

        float width = 0.0f;
        for (int i = FirstArtAtOrAfter(line); i < art.Count && i < artBoxes.Length && art[i].Line == line; i++)
        {
            if (artBoxes[i].Width > 0.0f)
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
        float tab = space * TabSize;
        Vector4 guide = skin.TextColor with { W = 0.08f };

        for (int index = first; index <= last; index++)
        {
            list.BeginPart(RowPart(ROW_GUIDES, rows[index]));
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
            for (int level = 1; level * TabSize <= depth; level++)
            {
                float x = MathF.Round(left + (level - 1) * tab);
                list.Rect(new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + 1.0f, top)), guide);
            }
        }
    }

    /// <summary>
    /// Tab as the key does it once nothing else wanted it (a snippet's next stop). A document indented with tabs gets a
    /// tab where the caret is (the document's own Tab goes to the next stop in spaces), and a selection over more than
    /// one line is indented a level either way.
    /// </summary>
    public void IndentOrTab()
    {
        if (ReadOnly)
            return;

        bool lines = document.HasSelection && document.SelectionStart.Line != document.SelectionEnd.Line;
        if (!lines && document.Indent.StartsWith('\t'))
            document.Insert("\t");
        else
            document.IndentLines();
    }

    // How many spaces in a line starts, a tab as TabSize of them, -1 for a line with nothing on it
    private int Indentation(int line)
    {
        string text = document.LineAt(line);
        int spaces = 0;
        foreach (char character in text)
        {
            if (character == ' ') spaces++;
            else if (character == '\t') spaces += TabSize;
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

    /// <summary>
    /// Lines put in or taken out of the text, what's wrong on the lines after it moved along with them, so it stays on
    /// the code it's about until whoever said it says it again. It stayed where it was and was about the line above.
    /// </summary>
    private void MoveDiagnostics(UITextPosition at, int by)
    {
        if (diagnostics.Count == 0 || by == 0)
            return;

        var moved = new UICodeDiagnostic[diagnostics.Count];
        for (int i = 0; i < moved.Length; i++)
        {
            UICodeDiagnostic found = diagnostics[i];
            int line = found.Line;
            if (by > 0 && (line > at.Line || (line == at.Line && found.Column >= at.Column)))
                line += by;
            else if (by < 0 && line > at.Line)
                line += by;
            else if (by < 0 && line > at.Line + by)
                line = at.Line + by;
            moved[i] = found with { Line = line };
        }

        diagnostics = moved;
        RebuildDiagnosticsSorted();
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

        // The worst on the line, an error over a warning, a hint is no mark at all
        bool marked = false;
        warning = true;
        for (int i = FirstDiagnosticAtOrAfter(line); i < diagnosticsSorted.Length && diagnosticsSorted[i].Line == line; i++)
        {
            if (diagnosticsSorted[i].Hint)
                continue;

            marked = true;
            warning &= diagnosticsSorted[i].Warning;
        }

        return marked;
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

/// <summary>How the caret of a code editor is drawn, VS Code's editor.cursorStyle.</summary>
public enum UICaretStyle
{
    /// <summary>A thin line before the character, the way it always was.</summary>
    Line,

    /// <summary>A block over the character, the character still written in it.</summary>
    Block,

    /// <summary>A line under the character.</summary>
    Underline
}
