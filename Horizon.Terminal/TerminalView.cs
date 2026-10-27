using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

using Horizon.Core.Processes;
using Horizon.Engine;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

using Silk.NET.Input;

namespace Horizon.UI.Components;

/// <summary>
/// A terminal on screen. A <see cref="TerminalScreen"/> drawn in the skin's font (or one of its own, Hack for
/// Dusk), as many columns and rows as fit, fed from a <see cref="PseudoTerminal"/> (<see cref="Start"/>, a shell or
/// anything else) or by hand (<see cref="Feed"/>, for a log or a test), with the keys going to the program as the
/// bytes a terminal sends (<see cref="TerminalKeys"/>), the mouse when the program asked for it and a selection by
/// cells when it didn't, the wheel, a scroll bar and a minimap down the right through the scrollback, a cursor that glides to where it is going and blinks, and
/// the bell as a flash. Nothing in its paint allocates, a row is drawn as runs of one style and the text of a run is
/// put together in a buffer kept for it. What the program writes arrives on the pty's thread and is queued, the
/// screen is only ever touched on the UI's thread, in <see cref="Update"/>.
/// </summary>
public partial class TerminalView : UIComponent, IDisposable
{
    private const float BLINK = 0.53f, BELL_FLASH = 0.25f, PADDING = 4.0f;

    /// <summary>How long the cursor takes to glide most of the way to its next cell unless told otherwise, see <see cref="CursorGlide"/>.</summary>
    public const float DEFAULT_CURSOR_GLIDE = 0.036f;
    private const int MAX_PENDING_CHUNKS = 4096;

    private TerminalScreen screen = new(80, 24);
    private PseudoTerminal? process;
    private readonly ConcurrentQueue<byte[]> arrived = new();
    private int drawnVersion = -1;

    // Where the cursor is drawn, gliding towards where it is, and the blink
    private Vector2 cursorAt;

    /// <summary>
    /// A picture drawn faintly behind the text, in the bottom right corner of <see cref="BackdropArea"/> (the pane itself
    /// when that's null), moved in from the corner by <see cref="BackdropOffset"/>, Dawn's picture behind the code in a
    /// terminal. A program with a screen of its own (the alternate screen) is drawn without it. A
    /// <see cref="TerminalPanel"/> sets all of these on its panes.
    /// </summary>
    public Horizon.Graphics.Texture? Backdrop { get; set; }

    /// <summary>How tall the picture is, as a share of the window's height. <see cref="BackdropOffset"/> is shares of its width and height.</summary>
    public float BackdropScale { get; set; } = 0.5f;

    public float BackdropOpacity { get; set; } = 0.2f;

    public Vector2 BackdropOffset { get; set; }

    /// <summary>How wide the picture is against its own shape, 1 for the shape it has (see <see cref="CodeEditor.BackdropStretch"/>).</summary>
    public float BackdropStretch { get; set; } = 1.0f;

    public UIRect? BackdropArea { get; set; }

    /// <summary>The middle of the cursor where it was last drawn, in the UI's units, for whoever throws sparks off it.</summary>
    public Vector2 CursorPoint => cursorAt + new Vector2(cellWidth, cellHeight) * 0.5f;
    private bool cursorPlaced;
    private float blink, bell;

    // How many rows up into the scrollback the view is, 0 for the bottom, and how many rows had gone off the top as of
    // the last update
    private int scrolled;
    private long scrolledOffSeen;

    // The selection by cell, in rows of the whole (scrollback and grid), anchor to end, inclusive
    private (int X, int Y) selectionAnchor, selectionEnd;
    private bool selecting, hasSelection;
    private float lastClick;
    private int clicks;

    // What the font came out as this paint, to turn points into cells
    private float cellWidth, cellHeight, paintedScale;
    private UIRect paintedArea;

    // A row's text as it is drawn, kept so the paint allocates nothing
    private char[] runBuffer = new char[512];

    private static readonly Vector4 White = Vector4.One;

    /// <summary>The screen, for whoever wants to read it (a test, a search).</summary>
    public TerminalScreen Screen => screen;

    /// <summary>The process in it, null while there is none.</summary>
    public PseudoTerminal? Process => process;

    /// <summary>The colours, the skin's by default, a theme's when set.</summary>
    public TerminalPalette Palette
    {
        get => palette;
        set
        {
            palette = value;
            screen.Palette = value;
        }
    }

    private TerminalPalette palette = new();

    /// <summary>Whether a program may set the clipboard (OSC 52, a yank in vim over SSH). Off unless the settings say so.</summary>
    public bool AllowClipboard { get; set; }

    /// <summary>The font the text is written in, null for the skin's. A monospaced one, or the columns don't line up.</summary>
    public UIFont? Font { get; set; }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>How many rows of what went off the top are kept.</summary>
    public int Scrollback { get => screen.MaxScrollback; set => screen.MaxScrollback = value; }

    /// <summary>Whether the cursor glides to its next cell rather than jumping, and the rest of the small movements.</summary>
    public bool Animated { get; set; } = true;

    /// <summary>
    /// How long the cursor takes to glide about two thirds of the way to its next cell, in seconds, 0 for it jumping
    /// there. It jumps whatever this says while <see cref="Animated"/> is off.
    /// </summary>
    public float CursorGlide { get; set; } = DEFAULT_CURSOR_GLIDE;

    /// <summary>Called when the program ends, with its exit code.</summary>
    public Action<int>? OnExited { get; set; }

    /// <summary>
    /// Asked about every key before the program gets it, true to keep it from the program. The keys an application
    /// wants for itself over its terminals (a new tab, a split), see <see cref="TerminalPanel.OnKey"/>.
    /// </summary>
    public Func<UIKeyPress, bool>? Intercept { get; set; }

    /// <summary>The profile it was started with, if any, for the panel's tabs and splits.</summary>
    public TerminalProfile? Profile { get; set; }

    private string? startedIn;

    /// <summary>The folder the shell is in as far as it has said (OSC 7), otherwise the one it was started in, null before it was.</summary>
    public string? WorkingDirectory => screen.WorkingDirectory ?? startedIn;

    /// <summary>
    /// Called with the bytes a key or a paste sent to the program, after they went, for whoever keeps a history of what
    /// was typed. What <see cref="Send"/> writes doesn't come through here, that is the application's own.
    /// </summary>
    public Action<string>? OnInput { get; set; }

    /// <summary>Called when the program sets the window's title.</summary>
    public Action<string>? OnTitle { get; set; }

    /// <summary>
    /// Something the shell asked the program round the terminal for, through the private OSC its integration says
    /// (<see cref="TerminalScreen.REQUEST_OSC"/>), <c>dawn;inline;C:\path</c> and the like. On the view's update.
    /// </summary>
    public Action<TerminalView, string>? OnRequest { get; set; }

    /// <summary>Called with a hyperlink's target when one is clicked with control held.</summary>
    public Action<string>? OnLink { get; set; }

    /// <summary>
    /// Called with a link found in the text (a URL, a file with its line, <see cref="TerminalLinks"/>) when it is clicked
    /// with control held, the ones a program marked with OSC 8 go to <see cref="OnLink"/>.
    /// </summary>
    public Action<TerminalLink>? OnTextLink { get; set; }

    /// <summary>Called with what the program wrote as it is taken in, on the UI's thread, for whoever reads it (the problem matchers).</summary>
    public Action<byte[]>? OnOutput { get; set; }

    /// <summary>What the program called its window.</summary>
    public string Title => screen.Title;

    /// <summary>How many columns and rows fit, as of the last paint.</summary>
    public int Columns => screen.Columns;

    public int Rows => screen.Rows;

    /// <summary>The selected text, empty for none.</summary>
    public string SelectedText => hasSelection ? SelectionText() : string.Empty;

    public bool HasSelection => hasSelection;

    protected override bool HitTestVisible => true;

    protected override bool Focusable => true;

    protected override bool KeepsTab => true;

    protected override bool TakesRawKeys => true;

    protected override void OnActivate() => Focus();

    public TerminalView()
    {
        Size = new Vector2(640, 360);
        screen.Bell = Rang;
        OnContextMenu = RightClicked;
        screen.Palette = palette;
        screen.Clipboard = text =>
        {
            if (AllowClipboard)
                UIClipboard.Copy(text);
        };
    }

    /// <summary>Starts a command in the terminal, the machine's shell when none is given, in a folder, ending whatever ran before.</summary>
    public PseudoTerminal Start(string? commandLine = null, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        string folder = workingDirectory ?? Environment.CurrentDirectory;
        return Attach(() => PseudoTerminal.Start(commandLine ?? PseudoTerminal.DefaultShell(), folder, screen.Columns, screen.Rows, environment), folder);
    }

    /// <summary>
    /// Shows a process that's running already in the terminal (a console Windows handed over), ending whatever ran before,
    /// and tells it how big the terminal is.
    /// </summary>
    public PseudoTerminal Attach(PseudoTerminal running, string? workingDirectory = null)
    {
        PseudoTerminal attached = Attach(() => running, workingDirectory ?? Environment.CurrentDirectory);
        attached.Resize(screen.Columns, screen.Rows);
        return attached;
    }

    // What both have, whatever ran before ended, the screen cleared, and the process listened to
    private PseudoTerminal Attach(Func<PseudoTerminal> make, string folder)
    {
        process?.Dispose();
        program?.Dispose();
        program = null;
        screen.Reset();
        scrolled = 0;
        hasSelection = false;

        startedIn = folder;
        PseudoTerminal started = make();
        // Windows' ConPTY lets the shell's marks through ahead of the text it paints, the screen settles them after
        screen.LateMarks = OperatingSystem.IsWindows();
        started.Output += Arrived;
        started.Exited += Ended;
        process = started;
        return started;
    }

    /// <summary>Runs a program with no process behind it in the terminal, the REPL or a TUI in HIDL, ending whatever ran before.</summary>
    public ITerminalProgram Start(ITerminalProgram started, string? workingDirectory = null)
    {
        process?.Dispose();
        process = null;
        program?.Dispose();
        screen.Reset();
        scrolled = 0;
        hasSelection = false;
        startedIn = workingDirectory ?? Environment.CurrentDirectory;
        program = started;
        started.Attach(this);
        return started;
    }

    /// <summary>A program with no process behind it ended, with its code, which ends the terminal the way a shell ending does.</summary>
    public void End(int code)
    {
        exitedWith = code;
        RequestFrame();
    }

    private ITerminalProgram? program;

    /// <summary>The program with no process behind it that runs in it, null for a shell or nothing.</summary>
    public ITerminalProgram? Program => program;

    /// <summary>Whether something runs in it, a shell or a program of its own.</summary>
    public bool IsRunning => process is { IsRunning: true } || program is { IsRunning: true };

    /// <summary>Bytes to whatever runs in the terminal, the shell's input or the program's.</summary>
    private void Write(string bytes)
    {
        if (process is not null)
            process.Write(bytes);
        else
            program?.Input(bytes);
    }

    /// <summary>Writes bytes to the screen as if the program had, for a log or a test.</summary>
    public void Feed(string text)
    {
        screen.Feed(text);
        SendResponses();
    }

    /// <summary>Sends text to the program as if typed.</summary>
    public void Send(string text) => Write(text);

    // On the pty's thread, queued for the next tick and the window woken for it
    private void Arrived(byte[] bytes)
    {
        if (arrived.Count < MAX_PENDING_CHUNKS)
            arrived.Enqueue(bytes);
        GameEngine.Instance?.WindowManager.Wake();
    }

    private void Ended(int code)
    {
        GameEngine.Instance?.WindowManager.Wake();
        exitedWith = code;
    }

    private int? exitedWith;

    protected override void Update(float dt)
    {
        base.Update(dt);
        program?.Update(dt);
        UpdateBadge();

        bool changed = false;
        while (arrived.TryDequeue(out byte[]? bytes))
        {
            // A frame the program asked to have shown whole (mode 2026) is held until its end or a tenth of a second,
            // then fed all at once, so a paint never shows half of it
            if (screen.SynchronizedOutput && heldFor < SYNC_MOST && bytes.AsSpan().IndexOf(SyncEnd) < 0)
            {
                held.Add(bytes);
                continue;
            }

            FeedHeld();
            screen.Feed(bytes);
            OnOutput?.Invoke(bytes);
            changed = true;
        }

        if (held.Count > 0)
        {
            heldFor += dt;
            if (heldFor >= SYNC_MOST || !screen.SynchronizedOutput)
            {
                FeedHeld();
                changed = true;
            }
        }

        // What the shell asked the program round it for, the dawn command
        while (screen.TryTakeRequest(out string request))
            OnRequest?.Invoke(this, request);

        UpdateFocusEvents();
        UpdateInline();
        screen.SettleIfQuiet();
        UpdateLine();
        UpdateCompletion();
        UpdateOutput();
        UpdateSendWhenReady(dt);

        if (IsHovered)
            FollowLink();

        if (changed)
        {
            SendResponses();
            if (screen.Title.Length > 0 && screen.Title != titleSaid)
            {
                titleSaid = screen.Title;
                OnTitle?.Invoke(titleSaid);
            }
        }

        // New output is followed at the bottom. Read up in the scrollback the view stays on what it shows, moved up by the
        // rows that went off the top under it, where it went down with them and the text slid out from under the reader
        long gone = screen.RowsScrolledOff - scrolledOffSeen;
        scrolledOffSeen = screen.RowsScrolledOff;
        if (scrolled > 0 && gone > 0)
            scrolled = (int)Math.Min(scrolled + gone, screen.Scrollback.Count);
        if (scrolled > screen.Scrollback.Count)
            scrolled = screen.Scrollback.Count;

        UpdateScrollBar(dt);

        if (exitedWith is { } code)
        {
            exitedWith = null;
            OnExited?.Invoke(code);
        }

        lastDt = dt;
        blink = (blink + dt) % (BLINK * 2.0f);
        if (bell > 0.0f)
            bell = MathF.Max(0.0f, bell - dt);

        if (changed || screen.Version != drawnVersion || bell > 0.0f || IsFocused)
            RequestFrame();
    }

    private const float SYNC_MOST = 0.1f;
    private static readonly byte[] SyncEnd = "\u001b[?2026l"u8.ToArray();
    private readonly List<byte[]> held = [];
    private float heldFor;

    private void FeedHeld()
    {
        for (int i = 0; i < held.Count; i++)
        {
            screen.Feed(held[i]);
            OnOutput?.Invoke(held[i]);
        }

        held.Clear();
        heldFor = 0.0f;
    }

    private bool focusSaid;

    /// <summary>Focus gained and lost said to a program that asked to hear it (mode 1004), vim reads files again on it.</summary>
    private void UpdateFocusEvents()
    {
        bool focused = IsFocused;
        if (focused == focusSaid)
            return;

        focusSaid = focused;
        if (screen.FocusEvents)
            Write(focused ? "\u001b[I" : "\u001b[O");
    }

    private string titleSaid = string.Empty;
    private float lastDt = 0.016f;

    // An application that sleeps between frames is asked for one, a game draws every frame anyway
    private static void RequestFrame() => GameEngine.Instance?.WindowManager.RequestFrames();

    private void SendResponses()
    {
        if (screen.Responses.Length == 0)
            return;

        // The answers the screen owes a program are a shell's, a program of our own never asks
        process?.Write(screen.Responses.ToString());
        screen.Responses.Clear();
    }

    /* Keys */

    protected override void OnKeyDown(in UIKeyPress press)
    {
        blink = 0.0f;
        if (CompletionKey(press) || Intercept?.Invoke(press) == true)
            return;

        var modifiers = ModifiersOf(press);

        // Control with shift and C or V copy and paste, control with C alone copies while something is selected and
        // is the program's otherwise, which is what Windows Terminal does
        if (press.Control && press.Key == Key.C && (press.Shift || hasSelection))
        {
            if (hasSelection)
                UIClipboard.Copy(SelectionText());
            return;
        }

        if (press.Control && press.Shift && press.Key == Key.V)
            return;

        // Shift with page up and down and the wheel read the scrollback
        if (press.Shift && press.Key is Key.PageUp or Key.PageDown && !screen.AlternateScreen)
        {
            ScrollBy(press.Key == Key.PageUp ? screen.Rows - 1 : -(screen.Rows - 1));
            return;
        }

        string? bytes = TerminalKeys.Key(press.Key, modifiers, screen.ApplicationCursorKeys, screen.KeyboardFlags);
        if (bytes is not null)
            Typed(bytes);
    }

    protected override void OnTextInput(char character)
    {
        // Control codes come through OnKeyDown, this is the typed text
        if (character < ' ' || character == UIKeyboard.DELETE)
            return;

        blink = 0.0f;
        var keyboard = GameEngine.Instance.Input.Keyboard;
        var modifiers = (keyboard.Alt ? TerminalKeys.Modifiers.Alt : 0) | (keyboard.Control ? TerminalKeys.Modifiers.Control : 0);
        Typed(TerminalKeys.Character(character.ToString(), modifiers, screen.KeyboardFlags));
    }

    protected override void OnPaste(string text)
    {
        if (text.Length == 0)
            return;

        // A paste is never a run of commands by accident, the newlines are the shell's to take
        Typed(TerminalKeys.Paste(text.Replace("\r\n", "\r").Replace('\n', '\r'), screen.BracketedPaste));
    }

    /// <summary>Text sent as if it had been typed, which is told to whoever follows what is typed, unlike <see cref="Send"/>.</summary>
    public void Type(string text) => Typed(text);

    private void Typed(string bytes)
    {
        scrolled = 0;
        hasSelection = false;
        Write(bytes);
        CompletionTyped(bytes);
        OnInput?.Invoke(bytes);
    }

    private static TerminalKeys.Modifiers ModifiersOf(in UIKeyPress press) =>
        (press.Shift ? TerminalKeys.Modifiers.Shift : 0) | (press.Alt ? TerminalKeys.Modifiers.Alt : 0) | (press.Control ? TerminalKeys.Modifiers.Control : 0) | (press.Super ? TerminalKeys.Modifiers.Super : 0);

    /* The pointer */

    protected override bool OnScroll(float delta)
    {
        if (delta == 0.0f)
            return false;

        // A full screen program hears the wheel as arrows, or as the mouse if it asked for that
        if (screen.AlternateScreen)
        {
            if (screen.MouseMode != TerminalMouseMode.None)
            {
                var (x, y) = CellAt(lastPointer);
                Write(TerminalKeys.Mouse(delta > 0.0f ? 64 : 65, x, y, 0, true, false, screen.MouseSgr));
            }
            else
            {
                string arrow = delta > 0.0f ? (screen.ApplicationCursorKeys ? "\u001bOA" : "\u001b[A") : (screen.ApplicationCursorKeys ? "\u001bOB" : "\u001b[B");
                for (int i = 0; i < 3; i++)
                    Write(arrow);
            }
            return true;
        }

        ScrollBy((int)(delta * 3.0f));
        return true;
    }

    private void ScrollBy(int rows)
    {
        scrolled = Math.Clamp(scrolled + rows, 0, screen.Scrollback.Count);
        RequestFrame();
    }

    private Vector2 lastPointer;

    protected override void OnPointerDown(Vector2 point)
    {
        Focus();
        lastPointer = point;
        if (BadgePressed(point))
            return;

        // The scroll bar and the minimap down the right, theirs whatever the program asked of the mouse
        if (PressScrollBar(point) || PressMinimap(point))
            return;

        var (x, y) = CellAt(point);
        var keyboard = GameEngine.Instance.Input.Keyboard;

        // A link under control, the program's mouse, or a selection
        if (keyboard.Control && LinkAt(x, y) is { } link)
        {
            OnLink?.Invoke(link);
            return;
        }

        if (keyboard.Control && TextLinkAt(x, y + RowOffset()) is { } found)
        {
            OnTextLink?.Invoke(found);
            return;
        }

        // The gutter, a block selected whole, and alt with a click, the shell's caret moved there
        if (GutterPressed(point, y + RowOffset()))
            return;

        if (keyboard.Alt && !keyboard.Shift && screen.MouseMode == TerminalMouseMode.None && MoveCaretTo(x, y))
            return;

        if (screen.MouseMode != TerminalMouseMode.None && !keyboard.Shift)
        {
            Write(TerminalKeys.Mouse(0, x, y, ModifiersNow(keyboard), true, false, screen.MouseSgr));
            reporting = true;
            return;
        }

        float now = (float)GameEngine.Instance.WindowManager.Tick / 1000.0f;
        clicks = now - lastClick < 0.35f && (x, y + RowOffset()) == selectionAnchor ? clicks + 1 : 1;
        lastClick = now;

        selectionAnchor = selectionEnd = (x, y + RowOffset());
        selecting = true;
        hasSelection = false;
        if (clicks == 2) SelectWord();
        if (clicks >= 3) SelectLine();
    }

    private bool reporting;

    protected override void OnPointerDrag(Vector2 point)
    {
        lastPointer = point;
        if (BadgeDragged(point))
            return;

        if (draggingBar)
        {
            DragScrollBar(point);
            return;
        }

        if (draggingMinimap)
        {
            DragMinimap(point);
            return;
        }

        // The track held pages on in the update, towards where the pointer is now
        if (paging != 0)
            return;

        var (x, y) = CellAt(point);
        if (reporting)
        {
            if (screen.MouseMode >= TerminalMouseMode.Drags)
                Write(TerminalKeys.Mouse(0, x, y, ModifiersNow(GameEngine.Instance.Input.Keyboard), true, true, screen.MouseSgr));
            return;
        }

        if (!selecting)
            return;

        var end = (x, y + RowOffset());
        if (end != selectionEnd)
        {
            selectionEnd = end;
            hasSelection = selectionEnd != selectionAnchor || clicks > 1;
            if (clicks == 2) SelectWord();
            RequestFrame();
        }
    }

    protected override void OnPointerUp(Vector2 point)
    {
        if (BadgeReleased(point) || ReleaseSide())
            return;

        var (x, y) = CellAt(point);
        if (reporting)
        {
            reporting = false;
            Write(TerminalKeys.Mouse(screen.MouseSgr ? 0 : 3, x, y, ModifiersNow(GameEngine.Instance.Input.Keyboard), false, false, screen.MouseSgr));
            return;
        }

        if (selecting)
            Selected();
        selecting = false;
    }

    private static TerminalKeys.Modifiers ModifiersNow(Horizon.Input.Keyboard keyboard) =>
        (keyboard.Shift ? TerminalKeys.Modifiers.Shift : 0) | (keyboard.Alt ? TerminalKeys.Modifiers.Alt : 0) | (keyboard.Control ? TerminalKeys.Modifiers.Control : 0);

    // The first row of the whole (scrollback and grid) that is at the top of the view
    private int RowOffset() => Math.Max(0, screen.Scrollback.Count - scrolled);

    /// <summary>The row of the whole (scrollback and screen) at the top of the view.</summary>
    public int TopRow => RowOffset();

    private (int X, int Y) CellAt(Vector2 point)
    {
        if (cellWidth <= 0.0f || cellHeight <= 0.0f)
            return (0, 0);

        int x = (int)MathF.Floor((point.X - paintedArea.Min.X) / cellWidth);
        int y = (int)MathF.Floor((paintedArea.Max.Y - point.Y) / cellHeight);
        return (Math.Clamp(x, 0, screen.Columns - 1), Math.Clamp(y, 0, screen.Rows - 1));
    }

    // The link in the text under the pointer while control is held, by the row of the whole and its cells, to underline
    private (int Row, int From, int To) underlined = (-1, 0, 0);
    private (int X, int Row) followedAt = (-1, -1);

    /// <summary>The link under the pointer worked out again when the pointer moved to another cell with control held.</summary>
    private void FollowLink()
    {
        bool control = GameEngine.Instance?.Input.Keyboard.Control == true;
        if (!control || Module is not { } module)
        {
            if (underlined.Row >= 0)
            {
                underlined = (-1, 0, 0);
                followedAt = (-1, -1);
                RequestFrame();
            }
            return;
        }

        var (x, y) = CellAt(module.ToLocal(module.Compositor.Pointer.Position));
        int row = y + RowOffset();
        if ((x, row) == followedAt)
            return;

        followedAt = (x, row);
        underlined = TextLinkAt(x, row) is { } link ? (row, link.From, link.To) : (-1, 0, 0);
        RequestFrame();
    }

    /// <summary>The link in the text at a cell of a row of the whole, null for none.</summary>
    private TerminalLink? TextLinkAt(int x, int row)
    {
        if (row < 0 || row >= screen.TotalRows)
            return null;

        return TerminalLinks.At(screen.RowAt(row).Text(), x, WorkingDirectory);
    }

    private string? LinkAt(int x, int y)
    {
        int row = y + RowOffset();
        if (row >= screen.TotalRows || x >= screen.Columns)
            return null;

        int link = screen.RowAt(row).Cells[x].Style.Link;
        return link > 0 && link < screen.Links.Count ? screen.Links[link] : null;
    }

    private void SelectWord()
    {
        var (ax, ay) = selectionAnchor;
        var (ex, ey) = selectionEnd;
        if ((ey, ex).CompareTo((ay, ax)) < 0)
            ((ax, ay), (ex, ey)) = ((ex, ey), (ax, ay));

        TerminalRow start = screen.RowAt(Math.Min(ay, screen.TotalRows - 1)), end = screen.RowAt(Math.Min(ey, screen.TotalRows - 1));
        while (ax > 0 && IsWord(start.Cells[ax - 1])) ax--;
        while (ex < end.Length - 1 && IsWord(end.Cells[ex + 1])) ex++;
        selectionAnchor = (ax, ay);
        selectionEnd = (ex, ey);
        hasSelection = true;
    }

    private static bool IsWord(in TerminalCell cell) => cell.CodePoint > ' ' && cell.CodePoint is not ('(' or ')' or '[' or ']' or '{' or '}' or '"' or '\'' or ',' or ';' or '|' or '<' or '>');

    private void SelectLine()
    {
        selectionAnchor = (0, selectionAnchor.Y);
        selectionEnd = (screen.Columns - 1, selectionEnd.Y);
        hasSelection = true;
    }

    private (int X, int Y, int X2, int Y2) SelectionBounds()
    {
        var (ax, ay) = selectionAnchor;
        var (ex, ey) = selectionEnd;
        return (ey, ex).CompareTo((ay, ax)) < 0 ? (ex, ey, ax, ay) : (ax, ay, ex, ey);
    }

    private string SelectionText()
    {
        var (x1, y1, x2, y2) = SelectionBounds();
        var text = new StringBuilder();
        for (int y = y1; y <= y2 && y < screen.TotalRows; y++)
        {
            TerminalRow row = screen.RowAt(y);
            text.Append(row.Text(y == y1 ? x1 : 0, y == y2 ? x2 + 1 : int.MaxValue));
            if (y < y2 && !row.Wrapped)
                text.Append('\n');
        }

        return text.ToString();
    }

    private bool CellSelected(int x, int row)
    {
        if (!hasSelection)
            return false;
        var (x1, y1, x2, y2) = SelectionBounds();
        if (row < y1 || row > y2) return false;
        if (row == y1 && x < x1) return false;
        if (row == y2 && x > x2) return false;
        return true;
    }

    /* Drawing */

    private UIFont FontOf(UISkin skin) => Font ?? skin.Font;

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    protected override Vector2 Measure(UISkin skin) => Size;

    protected override void Paint(UIDrawList list)
    {
        // Editing in place the editor is the pane, the shell carries on underneath out of sight
        if (inline is not null)
        {
            PaintInline(list);
            return;
        }

        list.PushFont(FontOf(list.Skin));
        PaintTerminal(list);
        list.PopFont();
    }

    private void PaintTerminal(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIFont font = FontOf(skin);
        float scale = ScaleOf(skin);
        paintedScale = scale;
        cellWidth = font.Measure("M", scale, markup: false).X;
        cellHeight = font.LineHeight * scale;
        // The gutter on the left once the shell says its marks, the blocks' marks are drawn in it. A pane with round corners
        // keeps its cells clear of them. The scroll bar and the minimap have room of their own down the right
        float pad = MathF.Max(PADDING, Radius * 0.6f);
        float header = ShowPaneTitle ? HeaderHeight(skin) : 0.0f;
        paintedArea = Bounds.Shrink(new UIEdges(screen.MarksSeen ? pad + GUTTER : pad, pad + header, pad + SideWidth, pad));
        PlaceSide();
        if (cellWidth <= 0.0f || cellHeight <= 0.0f)
            return;

        // As many cells as fit, the screen and the program told when that changes
        int columns = Math.Max(2, (int)(paintedArea.Width / cellWidth)), rows = Math.Max(1, (int)(paintedArea.Height / cellHeight));
        if (columns != screen.Columns || rows != screen.Rows)
        {
            screen.Resize(columns, rows);
            process?.Resize(columns, rows);
            program?.Resize(columns, rows);
            scrolled = Math.Min(scrolled, screen.Scrollback.Count);
        }

        TerminalPalette palette = Palette;
        Vector4 background = screen.ReverseVideo ? palette.Foreground : palette.Background;
        list.BeginPart(-1);
        list.RoundRect(Bounds, Radius, background);

        // The picture behind the text, placed against the area the panel says so a split shares the one picture
        if (Backdrop is { IsValid: true } picture && BackdropOpacity > 0.0f && !screen.AlternateScreen)
        {
            UIRect area = BackdropArea ?? Bounds;
            UIRect placed = CodeEditor.PlaceBackdrop(area, Module?.Frame ?? area, picture, BackdropScale, BackdropOffset, BackdropStretch);
            list.PushClip(Bounds);
            list.Image(picture, placed, new Vector4(1.0f, 1.0f, 1.0f, BackdropOpacity));
            list.PopClip();
        }
        if (bell > 0.0f)
            list.RoundOutline(Bounds, Radius, 2.0f, palette.Foreground with { W = bell / BELL_FLASH });

        int offset = RowOffset();
        float top = paintedArea.Max.Y;

        // The marks of the blocks in the gutter, outside the cells, and the matches of a find under the text
        PaintMarks(list, offset, rows, top, palette);

        // The cells in steps, a row's quads are another line's the moment a program scrolls (micro down a long file)
        // or the scrollback's oldest row goes, and blended between two ticks letters slid between rows
        list.PushClip(paintedArea);
        list.BeginSteps();
        for (int y = 0; y < rows; y++)
        {
            int index = offset + y;
            if (index >= screen.TotalRows)
                break;

            list.BeginPart(index);
            PaintRow(list, font, scale, screen.RowAt(index), index, top - y * cellHeight, palette);
        }
        list.EndSteps();

        // The cursor, gliding to its cell, as a block that blinks when it has the focus and an outline when it hasn't
        if (screen.CursorVisible && scrolled == 0)
        {
            Vector2 wanted = new(paintedArea.Min.X + screen.CursorX * cellWidth, top - (screen.CursorY + 1) * cellHeight);
            if (!cursorPlaced || !Animated || CursorGlide <= 0.0f)
            {
                cursorAt = wanted;
                cursorPlaced = true;
            }
            else
            {
                float dt = MathF.Min(0.05f, lastDt);
                cursorAt += (wanted - cursorAt) * (1.0f - MathF.Exp(-dt / CursorGlide));
                if (Vector2.DistanceSquared(cursorAt, wanted) < 0.25f)
                    cursorAt = wanted;
                else
                    RequestFrame();
            }

            list.BeginPart(-2);
            bool wide = screen[screen.CursorX, screen.CursorY].Style.Has(TerminalAttributes.Wide);
            var box = new UIRect(cursorAt, cursorAt + new Vector2(cellWidth * (wide ? 2 : 1), cellHeight));
            Vector4 ink = palette.Cursor;
            if (!IsFocused)
            {
                // Square, a cell's outline. The skin's corners rounded it, a fat pill in Dusk and a box in Dawn
                list.RoundOutline(box, 0.0f, 1.0f, ink);
            }
            else if (blink < BLINK || screen.CursorShape is 2 or 4 or 6)
            {
                switch (screen.CursorShape)
                {
                    case 3 or 4: list.Rect(new UIRect(box.Min, new Vector2(box.Max.X, box.Min.Y + MathF.Max(1.0f, cellHeight * 0.1f))), ink); break;
                    case 5 or 6: list.Rect(new UIRect(box.Min, new Vector2(box.Min.X + MathF.Max(1.0f, cellWidth * 0.15f), box.Max.Y)), ink); break;
                    default:
                        list.Rect(box, ink);
                        // The character under a block cursor in the background's colour, so it reads
                        PaintCell(list, font, scale, screen[screen.CursorX, screen.CursorY], cursorAt, palette, background, cursorInk: true);
                        break;
                }
            }
        }

        PaintCompletion(list, scale, top, palette);

        // The link under the pointer with control held, underlined in the cursor's colour
        if (underlined.Row >= offset && underlined.Row < offset + rows)
        {
            float lineTop = top - (underlined.Row - offset + 1) * cellHeight;
            list.Rect(new UIRect(new Vector2(paintedArea.Min.X + underlined.From * cellWidth, lineTop), new Vector2(paintedArea.Min.X + underlined.To * cellWidth, lineTop + MathF.Max(1.0f, cellHeight * 0.07f))), palette.Cursor);
        }

        list.PopClip();
        PaintMinimap(list, palette);
        PaintScrollBar(list, palette);
        PaintBadge(list, palette);
        list.EndParts();
        drawnVersion = screen.Version;
    }

    private void PaintRow(UIDrawList list, UIFont font, float scale, TerminalRow row, int rowIndex, float top, TerminalPalette palette)
    {
        float left = paintedArea.Min.X, baseline = top - cellHeight;
        int columns = Math.Min(row.Length, screen.Columns);
        if (runBuffer.Length < columns * 2)
            runBuffer = new char[columns * 2];

        // Backgrounds first, then the text, each in runs of one look
        int runStart = 0;
        while (runStart < columns)
        {
            ref TerminalCell first = ref row.Cells[runStart];
            int runEnd = runStart + 1;
            bool selected = CellSelected(runStart, rowIndex);
            while (runEnd < columns && row.Cells[runEnd].Style.LooksLike(first.Style) && CellSelected(runEnd, rowIndex) == selected)
                runEnd++;

            Vector4 back = BackgroundOf(first.Style, palette);
            if (selected)
                back = Blend(back, palette.Selection);
            if (back != palette.Background || selected || screen.ReverseVideo)
                list.Rect(new UIRect(new Vector2(left + runStart * cellWidth, baseline), new Vector2(left + runEnd * cellWidth, top)), back);

            runStart = runEnd;
        }

        // The typed line's colours split the runs where its kinds change, on the rows it is on
        bool onLine = lineRow >= 0 && rowIndex >= lineRow && rowIndex <= lineRow + (lineColumn + lineLength) / Math.Max(1, lineStride);
        runStart = 0;
        while (runStart < columns)
        {
            ref TerminalCell first = ref row.Cells[runStart];
            TerminalLineKind kind = onLine ? LineKindOf(rowIndex, runStart) : TerminalLineKind.None;
            byte code = OutputCodeOf(rowIndex, runStart);
            int runEnd = runStart + 1;
            while (runEnd < columns && row.Cells[runEnd].Style.LooksLike(first.Style) && (!onLine || LineKindOf(rowIndex, runEnd) == kind) && OutputCodeOf(rowIndex, runEnd) == code)
                runEnd++;

            if (!first.Style.Has(TerminalAttributes.Invisible))
            {
                Vector4 ink = ForegroundOf(first.Style, palette);
                bool underline = kind is TerminalLineKind.Path or TerminalLineKind.Folder;
                if (kind != TerminalLineKind.None)
                {
                    ink = LineInk(kind, first.Style, ink, palette);
                }
                else if (code != 0)
                {
                    ink = OutputInk(code, first.Style, ink, palette);
                    underline = (code & TerminalOutputRule.UNDERLINE) != 0 && first.Style.Foreground.IsDefault;
                }

                PaintRun(list, font, scale, row, runStart, runEnd, left, baseline, ink, first.Style);

                // a path that is there underlined, in the colour it is written in
                if (underline)
                {
                    float thickness = MathF.Max(1.0f, cellHeight * 0.06f);
                    list.Rect(new UIRect(new Vector2(left + runStart * cellWidth, baseline + thickness), new Vector2(left + runEnd * cellWidth, baseline + thickness * 2.0f)), ink);
                }
            }

            runStart = runEnd;
        }
    }

    // A run of cells of one style. Plain narrow text in one draw (every glyph of a monospaced font is a cell wide),
    // bold and wide characters a cell at a time so the grid holds whatever the font's advances are
    private void PaintRun(UIDrawList list, UIFont font, float scale, TerminalRow row, int from, int to, float left, float baseline, Vector4 ink, in TerminalStyle style)
    {
        bool bold = style.Has(TerminalAttributes.Bold), italic = style.Has(TerminalAttributes.Italic);
        UITextStyle textStyle = (bold ? UITextStyle.Bold : 0) | (italic ? UITextStyle.Italic : 0);
        float y = baseline + cellHeight;

        int at = from;
        while (at < to)
        {
            // A stretch of plain narrow cells goes in one piece
            int plainEnd = at;
            int length = 0;
            while (plainEnd < to && !bold && row.Cells[plainEnd].CodePoint < 0x10000 && row.Cells[plainEnd].Combining is null
                && !row.Cells[plainEnd].Style.Has(TerminalAttributes.Wide | TerminalAttributes.Continuation))
            {
                runBuffer[length++] = (char)row.Cells[plainEnd].CodePoint;
                plainEnd++;
            }

            if (length > 0)
            {
                // Trailing spaces draw nothing
                int drawn = length;
                while (drawn > 0 && runBuffer[drawn - 1] == ' ') drawn--;
                if (drawn > 0)
                    list.Text(runBuffer.AsSpan(0, drawn), new Vector2(left + at * cellWidth, y), scale, ink, false, textStyle, default);
                at = plainEnd;
                continue;
            }

            ref TerminalCell cell = ref row.Cells[at];
            if (!cell.IsContinuation && cell.CodePoint > ' ')
                PaintCell(list, font, scale, cell, new Vector2(left + at * cellWidth, baseline), Palette, ink, cursorInk: false);
            at++;
        }

        // Underlines and strikes, the length of the run
        if (style.Has(TerminalAttributes.Underline) && style.Underline != TerminalUnderline.None)
        {
            Vector4 lineInk = style.UnderlineColor.IsDefault ? ink : Palette.Of(style.UnderlineColor, true);
            float thickness = MathF.Max(1.0f, cellHeight * 0.06f);
            float lineY = baseline + thickness;
            var line = new UIRect(new Vector2(left + from * cellWidth, lineY), new Vector2(left + to * cellWidth, lineY + thickness));
            switch (style.Underline)
            {
                case TerminalUnderline.Double:
                    list.Rect(line, lineInk);
                    list.Rect(new UIRect(line.Min + new Vector2(0.0f, thickness * 2.0f), line.Max + new Vector2(0.0f, thickness * 2.0f)), lineInk);
                    break;
                case TerminalUnderline.Dotted or TerminalUnderline.Dashed:
                    float dash = style.Underline == TerminalUnderline.Dotted ? thickness : cellWidth * 0.5f;
                    for (float x = line.Min.X; x < line.Max.X; x += dash * 2.0f)
                        list.Rect(new UIRect(new Vector2(x, line.Min.Y), new Vector2(MathF.Min(x + dash, line.Max.X), line.Max.Y)), lineInk);
                    break;
                case TerminalUnderline.Curly:
                    // A wave out of short steps, up and down a pixel or two
                    float step = MathF.Max(1.0f, cellWidth * 0.25f);
                    int i = 0;
                    for (float x = line.Min.X; x < line.Max.X; x += step, i++)
                    {
                        float lift = (i % 4) switch { 0 => 0.0f, 1 => thickness, 2 => thickness * 2.0f, _ => thickness };
                        list.Rect(new UIRect(new Vector2(x, line.Min.Y + lift), new Vector2(MathF.Min(x + step, line.Max.X), line.Max.Y + lift)), lineInk);
                    }
                    break;
                default:
                    list.Rect(line, lineInk);
                    break;
            }
        }

        if (style.Has(TerminalAttributes.Strikethrough))
        {
            float thickness = MathF.Max(1.0f, cellHeight * 0.06f), mid = baseline + cellHeight * 0.5f;
            list.Rect(new UIRect(new Vector2(left + from * cellWidth, mid), new Vector2(left + to * cellWidth, mid + thickness)), ink);
        }

        if (style.Has(TerminalAttributes.Overline))
        {
            float thickness = MathF.Max(1.0f, cellHeight * 0.06f);
            list.Rect(new UIRect(new Vector2(left + from * cellWidth, y - thickness), new Vector2(left + to * cellWidth, y)), ink);
        }
    }

    // One cell on its own, a wide character, one with marks on it, or a bold one
    private void PaintCell(UIDrawList list, UIFont font, float scale, in TerminalCell cell, Vector2 corner, TerminalPalette palette, Vector4 ink, bool cursorInk)
    {
        if (cell.IsContinuation || cell.CodePoint <= ' ' && cell.Combining is null)
            return;

        int length = 0;
        if (cell.CodePoint < 0x10000)
        {
            runBuffer[length++] = (char)cell.CodePoint;
        }
        else
        {
            int v = cell.CodePoint - 0x10000;
            runBuffer[length++] = (char)(0xD800 + (v >> 10));
            runBuffer[length++] = (char)(0xDC00 + (v & 0x3FF));
        }

        if (cell.Combining is { } marks)
        {
            for (int i = 0; i < marks.Length && length < runBuffer.Length; i++)
                runBuffer[length++] = marks[i];
        }

        UITextStyle textStyle = (cell.Style.Has(TerminalAttributes.Bold) ? UITextStyle.Bold : 0) | (cell.Style.Has(TerminalAttributes.Italic) ? UITextStyle.Italic : 0);
        Vector4 colour = cursorInk ? ink : ForegroundOf(cell.Style, palette);
        list.Text(runBuffer.AsSpan(0, length), new Vector2(corner.X, corner.Y + cellHeight), scale, colour, false, textStyle, default);
    }

    private Vector4 ForegroundOf(in TerminalStyle style, TerminalPalette palette)
    {
        bool inverse = style.Has(TerminalAttributes.Inverse) != screen.ReverseVideo;
        Vector4 colour = inverse ? palette.Of(style.Background, false) : palette.Of(style.Foreground, true);
        if (inverse && style.Background.IsDefault) colour = palette.Background;

        // Bold brightens the plain eight, the way most terminals still do, dim fades
        if (style.Has(TerminalAttributes.Bold) && style.Foreground is { Kind: TerminalColorKind.Indexed, Index: < 8 } && !inverse)
            colour = palette.Ansi[style.Foreground.Index + 8];
        if (style.Has(TerminalAttributes.Dim))
            colour = Vector4.Lerp(colour, palette.Background, 0.4f);
        return colour;
    }

    private Vector4 BackgroundOf(in TerminalStyle style, TerminalPalette palette)
    {
        bool inverse = style.Has(TerminalAttributes.Inverse) != screen.ReverseVideo;
        if (inverse)
            return style.Foreground.IsDefault ? palette.Foreground : palette.Of(style.Foreground, true);
        return palette.Of(style.Background, false);
    }

    private static Vector4 Blend(Vector4 under, Vector4 over) => new(Vector3.Lerp(new Vector3(under.X, under.Y, under.Z), new Vector3(over.X, over.Y, over.Z), over.W), 1.0f);

    public void Dispose()
    {
        process?.Dispose();
        process = null;
        program?.Dispose();
        program = null;
        GC.SuppressFinalize(this);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("scrollback", () => (float)Scrollback, value => Scrollback = (int)value);
        Expose("animated", () => Animated, value => Animated = value);
        Expose("cursor_glide", () => CursorGlide, value => CursorGlide = MathF.Max(0.0f, value));
        Expose("minimap", () => Minimap, value => Minimap = value);
    }
}
