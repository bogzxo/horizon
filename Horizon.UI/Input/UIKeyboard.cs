using System.Collections.Concurrent;
using System.Diagnostics;

using Horizon.Engine;

using Silk.NET.Input;

namespace Horizon.UI;

/// <summary>
/// What is typed on the real keyboard, for whichever component has the focus.
/// The keyboard reports on the window's thread while the UI runs on the simulation thread, so the
/// characters wait in a queue in between.
/// <para>
/// Not everything that is typed is a character. The keys that edit (backspace, the arrows, control with A) are
/// put in the queue as characters of their own that no keyboard types, see the constants here. Whoever reads
/// the queue tells them apart from text by those. A key that edits and is held down is typed again and again
/// after a moment, the way the system does it for characters.
/// </para>
/// <para>
/// What is pasted (control with V) goes in as one <see cref="PASTE"/> mark with the whole text waiting next to it,
/// see <see cref="TryTakePaste"/>. A text box takes the first line of it, an editor can take a whole file.
/// </para>
/// </summary>
public static class UIKeyboard
{
    /// <summary>Stands for the backspace key in the queue.</summary>
    public const char BACKSPACE = '\b';

    /// <summary>Stands for either enter key in the queue.</summary>
    public const char ENTER = '\n';

    /// <summary>Stands for the tab key, which moves the focus on to the next component that takes it, and back with shift.</summary>
    public const char TAB = '\t';
    public const char SHIFT_TAB = '\u000B';

    /// <summary>Stands for the escape key, which closes what is open and gives the focus up.</summary>
    public const char ESCAPE = '\u001B';

    /// <summary>Stands for the delete key, which takes what is after the caret.</summary>
    public const char DELETE = '\u007F';

    /// <summary>Control with A, everything is selected.</summary>
    public const char SELECT_ALL = '\u0001';

    /// <summary>Control with C and with X, what is selected goes to the clipboard, and for X out of the text.</summary>
    public const char COPY = '\u0003';
    public const char CUT = '\u0018';

    /// <summary>Control with V, what is on the clipboard is typed, see <see cref="TryTakePaste"/> for the text itself.</summary>
    public const char PASTE = '\u0016';

    /// <summary>The caret goes somewhere. A character or (with control) a word to either side, or to either end.</summary>
    public const char LEFT = '\u0011';
    public const char RIGHT = '\u0012';
    public const char WORD_LEFT = '\u0013';
    public const char WORD_RIGHT = '\u0014';
    public const char HOME = '\u0002';
    public const char END = '\u0005';

    /// <summary>The same with shift held, which selects what the caret goes over.</summary>
    public const char SELECT_LEFT = '\u000E';
    public const char SELECT_RIGHT = '\u000F';
    public const char SELECT_WORD_LEFT = '\u0010';
    public const char SELECT_WORD_RIGHT = '\u0017';
    public const char SELECT_HOME = '\u0006';
    public const char SELECT_END = '\u0007';

    /// <summary>Control with backspace and with delete. A whole word goes.</summary>
    public const char WORD_BACKSPACE = '\u0019';
    public const char WORD_DELETE = '\u001A';

    /*
     * The keys only text of more than one line has a use for, a code editor. They are characters out of the private
     * use area, which no keyboard types, so a text box that doesn't know them only has to leave the area alone
     * (see IsEditingKey).
     */

    /// <summary>The caret goes a line up or down, and the same with shift held.</summary>
    public const char UP = '\uE000';
    public const char DOWN = '\uE001';
    public const char SELECT_UP = '\uE002';
    public const char SELECT_DOWN = '\uE003';

    /// <summary>A screen's worth up or down.</summary>
    public const char PAGE_UP = '\uE004';
    public const char PAGE_DOWN = '\uE005';
    public const char SELECT_PAGE_UP = '\uE006';
    public const char SELECT_PAGE_DOWN = '\uE007';

    /// <summary>Control with home and with end, the start and the end of the whole text rather than of the line.</summary>
    public const char TEXT_START = '\uE008';
    public const char TEXT_END = '\uE009';
    public const char SELECT_TEXT_START = '\uE00A';
    public const char SELECT_TEXT_END = '\uE00B';

    /// <summary>Control with Z, and with Y or shift and Z.</summary>
    public const char UNDO = '\uE00C';
    public const char REDO = '\uE00D';

    /// <summary>Control with slash, the lines of the selection are made comments or made code again.</summary>
    public const char TOGGLE_COMMENT = '\uE00E';

    /// <summary>Control, shift and D, the line (or the selection) once more after itself.</summary>
    public const char DUPLICATE = '\uE00F';

    /// <summary>Control with D, the word at the caret selected, then the next place it is gets a caret of its own as well.</summary>
    public const char NEXT_OCCURRENCE = '\uE013';

    /// <summary>Control, alt and up or down, another caret on the line over (or under) the carets.</summary>
    public const char CARET_ABOVE = '\uE014';
    public const char CARET_BELOW = '\uE015';

    /// <summary>Shift, alt and I, a caret at the end of every line of the selection. It was control, shift and L, VS Code has it here.</summary>
    public const char CARETS_AT_LINE_ENDS = '\uE016';

    /// <summary>Control, shift and L, a caret on every place the selection (or the word at the caret) is, each with it selected.</summary>
    public const char SELECT_ALL_OCCURRENCES = '\uE01C';

    /// <summary>Control with L, the caret's line selected, and the next one as well every time again.</summary>
    public const char SELECT_LINE = '\uE01B';

    /// <summary>Shift, alt and up or down, the line (or the lines of the selection) copied above or below itself.</summary>
    public const char COPY_LINES_UP = '\uE019';
    public const char COPY_LINES_DOWN = '\uE01A';

    /// <summary>Shift, alt and A, the selection put in a block comment or taken out of one.</summary>
    public const char TOGGLE_BLOCK_COMMENT = '\uE01D';

    /// <summary>Control, shift and [ folds what the caret is inside of, and ] opens it again.</summary>
    public const char FOLD = '\uE017';
    public const char UNFOLD = '\uE018';

    /// <summary>Control with space, what is being typed is offered what it could be.</summary>
    public const char COMPLETE = '\uE012';

    /// <summary>Alt with up and with down, the line (or the lines of the selection) swapped with the one above or below.</summary>
    public const char MOVE_LINES_UP = '\uE010';
    public const char MOVE_LINES_DOWN = '\uE011';

    // What a key that types nothing at all comes to, never put in the queue
    private const char NOTHING = '\0';

    /// <summary>Whether a character is one of the keys of the private use area above rather than anything typed.</summary>
    public static bool IsEditingKey(char character) => character is >= '\uE000' and <= '\uE0FF';

    // Nobody reads the queue while nothing has the focus, so it has to stop growing by itself
    private const int MAX_PENDING = 256;

    // How long (in milliseconds) a key that edits is held before it starts repeating, and how long between two repeats
    private const long REPEAT_DELAY = 420;
    private const long REPEAT_INTERVAL = 34;

    private static readonly ConcurrentQueue<char> typed = new();

    // The keys themselves, for a component that takes them raw (see RawKeys), and the one held for repeating
    private static readonly ConcurrentQueue<UIKeyPress> rawKeys = new();
    private static volatile bool rawMode;
    private static UIKeyPress heldRaw;
    private static long rawRepeatDue;
    private static volatile bool altHeld;
    private static bool hooked;

    // The texts that were pasted, one for every PASTE in the queue of characters and in the same order
    private static readonly ConcurrentQueue<string> pasted = new();

    // The key that is being held to have it repeated, as what it puts in the queue (0 for none), and when it is next due
    private static int heldCode;
    private static Key heldKey;
    private static long repeatDue;

    // What is waiting to be put on the clipboard, which only the thread of the window may touch
    private static string? clipboardPending;
    private static IKeyboard? clipboardOwner;

    // The tick whose turn of the mouse wheel somebody already used, -1 for none
    private static long scrollUsedOn = -1;

    /// <summary>
    /// Starts listening to the keyboards. Only the first call does anything, and it has to be made once
    /// the window exists.
    /// </summary>
    public static void Hook()
    {
        if (hooked || GameEngine.Instance.Input.Native is not { } input)
            return;

        hooked = true;

        // The clipboard is the window's, it is only ever touched on its thread
        GameEngine.Instance.WindowManager.EventsProcessed += FlushClipboard;

        foreach (var keyboard in input.Keyboards)
        {
            clipboardOwner ??= keyboard;

            keyboard.KeyChar += (_, character) => Push(character);
            keyboard.KeyDown += (pressed, key, _) => OnKeyDown(pressed, key);
            keyboard.KeyUp += (_, key, _) =>
            {
                if (key == heldKey)
                    Volatile.Write(ref heldCode, 0);
                if (key == heldRaw.Key)
                    heldRaw = default;
                if (key is Key.AltLeft or Key.AltRight)
                    altHeld = false;
            };
        }
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key)
    {
        bool control = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
        bool alt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
        bool super = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);
        if (key is Key.AltLeft or Key.AltRight)
            altHeld = true;

        // What copies, pastes and undoes, command on a Mac and control everywhere else (see Keyboard.Primary). A
        // terminal still gets control as control, it's the shell's
        bool mac = Horizon.Input.Keyboard.PrimaryIsCommand;
        bool primary = mac ? super : control;

        // A component that takes the keys themselves gets every one of them, and none of the codes below. Control with V
        // (command on a Mac) is still a paste, a terminal pastes with it too
        if (rawMode)
        {
            if (key == Key.V && primary)
            {
                Paste(keyboard);
                return;
            }

            if (key is Key.ControlLeft or Key.ControlRight or Key.ShiftLeft or Key.ShiftRight or Key.AltLeft or Key.AltRight or Key.SuperLeft or Key.SuperRight)
                return;

            var press = new UIKeyPress(key, control, shift, alt, super);
            if (rawKeys.Count < MAX_PENDING)
                rawKeys.Enqueue(press);
            heldRaw = press;
            rawRepeatDue = Stopwatch.GetTimestamp() + REPEAT_DELAY * Stopwatch.Frequency / 1000;
            return;
        }

        // What the key means, and whether holding it does it over and over
        (char code, bool repeats) = Meaning(key, primary, shift, alt, mac);

        if (key == Key.V && primary)
        {
            Paste(keyboard);
            return;
        }

        if (code == '\0')
            return;

        Push(code);

        if (repeats)
        {
            heldKey = key;
            repeatDue = Stopwatch.GetTimestamp() + REPEAT_DELAY * Stopwatch.Frequency / 1000;
            Volatile.Write(ref heldCode, code);
        }
    }

    /// <summary>
    /// What a key pressed comes to in the queue, and whether holding it does it over and over, nothing (0) for a key that
    /// types nothing. Primary is control, command on a Mac. On its own so the keys can be tested without a keyboard.
    /// </summary>
    public static (char Code, bool Repeats) Meaning(Key key, bool primary, bool shift, bool alt, bool mac) => key switch
    {
        // A Mac's caret keys: option goes a word, command to the end of the line (or with up and down, of the text)
        Key.Backspace when mac && alt => (WORD_BACKSPACE, true),
        Key.Delete when mac && alt => (WORD_DELETE, true),
        Key.Left when mac && primary => (shift ? SELECT_HOME : HOME, false),
        Key.Right when mac && primary => (shift ? SELECT_END : END, false),
        Key.Left when mac && alt => (shift ? SELECT_WORD_LEFT : WORD_LEFT, true),
        Key.Right when mac && alt => (shift ? SELECT_WORD_RIGHT : WORD_RIGHT, true),
        Key.Up when mac && primary && !alt => (shift ? SELECT_TEXT_START : TEXT_START, false),
        Key.Down when mac && primary && !alt => (shift ? SELECT_TEXT_END : TEXT_END, false),
        Key.Backspace => (primary ? WORD_BACKSPACE : BACKSPACE, true),
        Key.Delete => (primary ? WORD_DELETE : DELETE, true),
        // With control (command on a Mac) enter is somebody's shortcut (put the code in), not a new line
        Key.Enter or Key.KeypadEnter => (primary ? NOTHING : ENTER, false),
        // With control (command on a Mac) tab is somebody's shortcut too (the next document), not an indent
        Key.Tab => (primary ? NOTHING : shift ? SHIFT_TAB : TAB, false),
        Key.Escape => (ESCAPE, false),
        // Alt with left and right is somebody's going back and forward, not the caret's
        Key.Left or Key.Right when alt && !primary => (NOTHING, false),
        Key.Left => ((shift, primary) switch { (true, true) => SELECT_WORD_LEFT, (true, false) => SELECT_LEFT, (false, true) => WORD_LEFT, _ => LEFT }, true),
        Key.Right => ((shift, primary) switch { (true, true) => SELECT_WORD_RIGHT, (true, false) => SELECT_RIGHT, (false, true) => WORD_RIGHT, _ => RIGHT }, true),
        Key.Home => ((shift, primary) switch { (true, true) => SELECT_TEXT_START, (false, true) => TEXT_START, (true, false) => SELECT_HOME, _ => HOME }, false),
        Key.End => ((shift, primary) switch { (true, true) => SELECT_TEXT_END, (false, true) => TEXT_END, (true, false) => SELECT_END, _ => END }, false),
        Key.Up when alt && primary => (CARET_ABOVE, true),
        Key.Down when alt && primary => (CARET_BELOW, true),
        Key.Up when alt && shift => (COPY_LINES_UP, true),
        Key.Down when alt && shift => (COPY_LINES_DOWN, true),
        Key.Up when alt => (MOVE_LINES_UP, true),
        Key.Down when alt => (MOVE_LINES_DOWN, true),
        Key.Up => (shift ? SELECT_UP : UP, true),
        Key.Down => (shift ? SELECT_DOWN : DOWN, true),
        Key.PageUp => (shift ? SELECT_PAGE_UP : PAGE_UP, true),
        Key.PageDown => (shift ? SELECT_PAGE_DOWN : PAGE_DOWN, true),
        Key.Z when primary && shift => (REDO, true),
        Key.Z when primary => (UNDO, true),
        Key.Y when primary => (REDO, true),
        Key.Slash when primary => (TOGGLE_COMMENT, false),
        Key.D when primary && shift => (DUPLICATE, false),
        Key.D when primary => (NEXT_OCCURRENCE, false),
        Key.L when primary && shift => (SELECT_ALL_OCCURRENCES, false),
        Key.L when primary => (SELECT_LINE, true),
        Key.I when alt && shift && !primary => (CARETS_AT_LINE_ENDS, false),
        Key.A when alt && shift && !primary => (TOGGLE_BLOCK_COMMENT, false),
        Key.LeftBracket when primary && shift => (FOLD, false),
        Key.RightBracket when primary && shift => (UNFOLD, false),
        Key.Space when primary => (COMPLETE, false),
        Key.A when primary => (SELECT_ALL, false),
        Key.C when primary => (COPY, false),
        Key.X when primary => (CUT, false),
        _ => ('\0', false)
    };

    /// <summary>
    /// Helper to queue what is on the clipboard, as one paste. Whoever has the focus takes what it can use of it.
    /// We are on the thread of the window here, which is the one that may ask for it.
    /// </summary>
    private static void Paste(IKeyboard keyboard)
    {
        string text;
        try
        {
            text = keyboard.ClipboardText ?? string.Empty;
        }
        catch (Exception)
        {
            // Something on the clipboard that isn't text
            return;
        }

        if (text.Length == 0 || typed.Count >= MAX_PENDING)
            return;

        pasted.Enqueue(text);
        typed.Enqueue(PASTE);
    }

    /// <summary>
    /// The text of the paste that is next in line, once a <see cref="PASTE"/> has been read from the queue. Reading the
    /// mark and taking its text go together, or the two queues drift apart.
    /// </summary>
    public static bool TryTakePaste(out string text) => pasted.TryDequeue(out text!);

    /// <summary>
    /// Types the key that is being held down again if it is time to, called once per update by every UI. This
    /// is what has backspace go on taking characters for as long as it is held.
    /// </summary>
    public static void Repeat()
    {
        // A raw key held is pressed again too, the same way
        if (rawMode && heldRaw.Key != Key.Unknown)
        {
            long at = Stopwatch.GetTimestamp();
            if (at >= Volatile.Read(ref rawRepeatDue))
            {
                Volatile.Write(ref rawRepeatDue, at + REPEAT_INTERVAL * Stopwatch.Frequency / 1000);
                if (rawKeys.Count < MAX_PENDING)
                    rawKeys.Enqueue(heldRaw);
            }
        }

        int code = Volatile.Read(ref heldCode);
        if (code == 0)
            return;

        long now = Stopwatch.GetTimestamp();
        if (now < Volatile.Read(ref repeatDue))
            return;

        Volatile.Write(ref repeatDue, now + REPEAT_INTERVAL * Stopwatch.Frequency / 1000);
        Push((char)code);
    }

    /// <summary>
    /// Asks for text to be put on the clipboard, from any thread. It gets there the next time the thread of the window
    /// has heard from the system, which is within a millisecond or so (see <see cref="FlushClipboard"/>).
    /// </summary>
    public static void Copy(string text) => Volatile.Write(ref clipboardPending, text);

    private static int pasteWanted;

    /// <summary>
    /// Asks for what is on the clipboard to be pasted into whatever has the focus, as Ctrl+V would, from any thread (a
    /// right click in a terminal). The clipboard is only read on the thread of the window, so it arrives in a moment.
    /// </summary>
    public static void RequestPaste()
    {
        Volatile.Write(ref pasteWanted, 1);
        GameEngine.Instance?.WindowManager.Wake();
    }

    /// <summary>Puts what <see cref="Copy"/> was last given on the clipboard, and pastes when that was asked for. Thread of the window only.</summary>
    public static void FlushClipboard()
    {
        if (Volatile.Read(ref pasteWanted) == 1 && clipboardOwner is not null && Interlocked.Exchange(ref pasteWanted, 0) == 1)
            Paste(clipboardOwner);

        if (Volatile.Read(ref clipboardPending) is null || clipboardOwner is null)
            return;

        string? text = Interlocked.Exchange(ref clipboardPending, null);
        if (text is null)
            return;

        try
        {
            clipboardOwner.ClipboardText = text;
        }
        catch (Exception)
        {
            // Somebody else has the clipboard open, the text stays where it is
        }
    }

    /// <summary>Takes the next typed character, in the order they were typed.</summary>
    public static bool TryRead(out char character) => typed.TryDequeue(out character);

    /// <summary>The next key pressed while a component that takes them raw had the focus, see <see cref="RawKeys"/>.</summary>
    public static bool TryReadKey(out UIKeyPress press) => rawKeys.TryDequeue(out press);

    /// <summary>
    /// Whether the keys go out as themselves (<see cref="TryReadKey"/>) rather than as the codes above, which the
    /// compositor sets while the focus is a component that takes them so (a terminal, which wants control with every
    /// letter and the function keys as the program behind it does). Typed characters still come through
    /// <see cref="TryRead"/>, less the ones typed with alt held, those are the raw key's to send with an escape in front.
    /// </summary>
    public static bool RawKeys
    {
        get => rawMode;
        set
        {
            if (rawMode == value)
                return;
            rawMode = value;
            heldRaw = default;
            if (!value)
                rawKeys.Clear();
        }
    }

    /// <summary>
    /// How far the mouse wheel turned for this update, in notches (positive is away from the user), or nothing if
    /// another UI already used it. Every UI gets to look at it, the first one with a use for it says so with
    /// <see cref="UseScroll"/>. A UI that has nothing under the pointer used to take it anyway and leave the one that
    /// did with sod all. Simulation thread.
    /// </summary>
    public static float PeekScroll() =>
        Volatile.Read(ref scrollUsedOn) == GameEngine.Instance.WindowManager.Tick ? 0.0f : GameEngine.Instance.Input.Mouse.Scroll;

    /// <summary>
    /// The same, as the window heard it, whether the wheel is being withheld from the game or not. For the UI that
    /// is doing the withholding (the Skyline debugger), which would otherwise withhold the wheel from itself.
    /// </summary>
    internal static float PeekWindowScroll() =>
        Volatile.Read(ref scrollUsedOn) == GameEngine.Instance.WindowManager.Tick ? 0.0f : GameEngine.Instance.Input.Mouse.WindowScroll;

    /// <summary>Says the turn of the wheel of this update was used, nobody else is to scroll with it.</summary>
    public static void UseScroll() => Volatile.Write(ref scrollUsedOn, GameEngine.Instance.WindowManager.Tick);

    /// <summary>Throws away whatever was typed while nothing was listening, pastes and all.</summary>
    public static void Clear()
    {
        typed.Clear();
        pasted.Clear();
    }

    private static void Push(char character)
    {
        if (rawMode && altHeld && character >= ' ')
            return;

        if (typed.Count < MAX_PENDING)
            typed.Enqueue(character);
    }
}

/// <summary>A key as it was pressed, for a component that takes the keys themselves (see <see cref="UIKeyboard.RawKeys"/>).</summary>
public readonly record struct UIKeyPress(Key Key, bool Control, bool Shift, bool Alt, bool Super);

/// <summary>
/// The clipboard of the system, for whoever has something to put on it that isn't the selection of a text box
/// (an editor copying components as code). What is on it comes back through control with V, see
/// <see cref="UICompositor.Pasted"/> and <see cref="Components.UIComponent.OnPaste"/>.
/// </summary>
public static class UIClipboard
{
    /// <summary>Puts text on the clipboard. From any thread, it is there within a moment.</summary>
    public static void Copy(string text) => UIKeyboard.Copy(text);

    /// <summary>Pastes what is on the clipboard into whatever has the focus, in a moment, from any thread.</summary>
    public static void Paste() => UIKeyboard.RequestPaste();
}
