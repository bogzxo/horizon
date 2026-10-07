using System.Collections.Concurrent;
using System.Diagnostics;

using Horizon.Engine;

using Silk.NET.Input;

namespace Horizon.Rendering.UIX;

/// <summary>
/// What is typed on the real keyboard, for whichever component has the focus.
/// The keyboard reports on the window's thread while the UI runs on the logic thread, so the
/// characters wait in a queue in between.
/// <para>
/// Not everything that is typed is a character. The keys that edit (backspace, the arrows, control with A) are
/// put in the queue as characters of their own that no keyboard types, see the constants here. Whoever reads
/// the queue tells them apart from text by those. A key that edits and is held down is typed again and again
/// after a moment, the way the system does it for characters.
/// </para>
/// </summary>
internal static class UIKeyboard
{
    /// <summary>Stands for the backspace key in the queue.</summary>
    public const char BACKSPACE = '\b';

    /// <summary>Stands for either enter key in the queue.</summary>
    public const char ENTER = '\n';

    /// <summary>Stands for the delete key, which takes what is after the caret.</summary>
    public const char DELETE = '\u007F';

    /// <summary>Control with A: everything is selected.</summary>
    public const char SELECT_ALL = '';

    /// <summary>Control with C and with X: what is selected goes to the clipboard, and for X out of the text.</summary>
    public const char COPY = '';
    public const char CUT = '';

    /// <summary>The caret goes somewhere. A character or (with control) a word to either side, or to either end.</summary>
    public const char LEFT = '';
    public const char RIGHT = '';
    public const char WORD_LEFT = '';
    public const char WORD_RIGHT = '';
    public const char HOME = '';
    public const char END = '';

    /// <summary>The same with shift held, which selects what the caret goes over.</summary>
    public const char SELECT_LEFT = '';
    public const char SELECT_RIGHT = '';
    public const char SELECT_WORD_LEFT = '';
    public const char SELECT_WORD_RIGHT = '';
    public const char SELECT_HOME = '';
    public const char SELECT_END = '';

    /// <summary>Control with backspace and with delete. A whole word goes.</summary>
    public const char WORD_BACKSPACE = '';
    public const char WORD_DELETE = '';

    // Nobody reads the queue while nothing has the focus, so it has to stop growing by itself. Big enough for
    // something that is pasted
    private const int MAX_PENDING = 1024;

    // How long (in milliseconds) a key that edits is held before it starts repeating, and how long between two repeats
    private const long REPEAT_DELAY = 420;
    private const long REPEAT_INTERVAL = 34;

    private static readonly ConcurrentQueue<char> typed = new();
    private static bool hooked;

    // The key that is being held to have it repeated, as what it puts in the queue (0 for none), and when it is next due
    private static int heldCode;
    private static Key heldKey;
    private static long repeatDue;

    // What is waiting to be put on the clipboard, which only the thread of the window may touch
    private static string? clipboardPending;
    private static IKeyboard? clipboardOwner;

    // The notches the mouse wheel has turned since anybody last asked, in thousandths so they can be swapped whole.
    private static int scrolled;

    /// <summary>
    /// Starts listening to the keyboards. Only the first call does anything; has to be made once
    /// the window exists.
    /// </summary>
    public static void Hook()
    {
        if (hooked || GameEngine.Instance.Input.Native is not { } input)
            return;

        hooked = true;

        foreach (var mouse in input.Mice)
            mouse.Scroll += (_, wheel) => Interlocked.Add(ref scrolled, (int)(wheel.Y * 1000.0f));

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
    /// Helper to type what is on the clipboard, as if it had been typed by hand. Whoever has the focus takes
    /// what it can use of it. We are on the thread of the window here, which is the one that may ask for it.
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

        foreach (char character in text)
        {
            // One line of it. A text box has no use for the rest
            if (character is '\r' or '\n')
                break;

            if (!char.IsControl(character))
                Push(character);
        }
    }

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
    /// Asks for text to be put on the clipboard, from any thread. It gets there the next time
    /// <see cref="FlushClipboard"/> runs on the thread of the window.
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
    /// Takes how far the mouse wheel has turned since the last time this was asked, in notches. Positive is
    /// away from the user.
    /// </summary>
    public static float TakeScroll() => Interlocked.Exchange(ref scrolled, 0) / 1000.0f;

    /// <summary>Throws away whatever was typed while nothing was listening.</summary>
    public static void Clear() => typed.Clear();

    private static void Push(char character)
    {
        if (typed.Count < MAX_PENDING)
            typed.Enqueue(character);
    }
}
