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
/// see <see cref="TryTakePaste"/>: a text box takes the first line of it, an editor can take a whole file.
/// </para>
/// </summary>
internal static class UIKeyboard
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

    /// <summary>Control with A: everything is selected.</summary>
    public const char SELECT_ALL = '\u0001';

    /// <summary>Control with C and with X: what is selected goes to the clipboard, and for X out of the text.</summary>
    public const char COPY = '\u0003';
    public const char CUT = '\u0018';

    /// <summary>Control with V: what is on the clipboard is typed, see <see cref="TryTakePaste"/> for the text itself.</summary>
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

    // Nobody reads the queue while nothing has the focus, so it has to stop growing by itself
    private const int MAX_PENDING = 256;

    // How long (in milliseconds) a key that edits is held before it starts repeating, and how long between two repeats
    private const long REPEAT_DELAY = 420;
    private const long REPEAT_INTERVAL = 34;

    private static readonly ConcurrentQueue<char> typed = new();
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
    /// Starts listening to the keyboards. Only the first call does anything; has to be made once
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
            };
        }
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key)
    {
        bool control = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);

        // What the key means, and whether holding it does it over and over
        (char code, bool repeats) = key switch
        {
            Key.Backspace => (control ? WORD_BACKSPACE : BACKSPACE, true),
            Key.Delete => (control ? WORD_DELETE : DELETE, true),
            Key.Enter or Key.KeypadEnter => (ENTER, false),
            Key.Tab => (shift ? SHIFT_TAB : TAB, false),
            Key.Escape => (ESCAPE, false),
            Key.Left => ((shift, control) switch { (true, true) => SELECT_WORD_LEFT, (true, false) => SELECT_LEFT, (false, true) => WORD_LEFT, _ => LEFT }, true),
            Key.Right => ((shift, control) switch { (true, true) => SELECT_WORD_RIGHT, (true, false) => SELECT_RIGHT, (false, true) => WORD_RIGHT, _ => RIGHT }, true),
            Key.Home => (shift ? SELECT_HOME : HOME, false),
            Key.End => (shift ? SELECT_END : END, false),
            Key.A when control => (SELECT_ALL, false),
            Key.C when control => (COPY, false),
            Key.X when control => (CUT, false),
            _ => ('\0', false)
        };

        if (key == Key.V && control)
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

    /// <summary>Puts what <see cref="Copy"/> was last given on the clipboard. Thread of the window only.</summary>
    public static void FlushClipboard()
    {
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

    /// <summary>
    /// How far the mouse wheel turned for this update, in notches (positive is away from the user), or nothing if
    /// another UI already used it. Every UI gets to look at it, the first one with a use for it says so with
    /// <see cref="UseScroll"/>: a UI that has nothing under the pointer used to take it anyway and leave the one that
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
        if (typed.Count < MAX_PENDING)
            typed.Enqueue(character);
    }
}

/// <summary>
/// The clipboard of the system, for whoever has something to put on it that isn't the selection of a text box
/// (an editor copying components as code). What is on it comes back through control with V, see
/// <see cref="UICompositor.Pasted"/> and <see cref="Components.UIComponent.OnPaste"/>.
/// </summary>
public static class UIClipboard
{
    /// <summary>Puts text on the clipboard. From any thread, it is there within a moment.</summary>
    public static void Copy(string text) => UIKeyboard.Copy(text);
}
