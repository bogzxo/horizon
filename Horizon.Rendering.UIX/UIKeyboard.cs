using System.Collections.Concurrent;

using Horizon.Engine;

using Silk.NET.Input;

namespace Horizon.Rendering.UIX;

/// <summary>
/// What is typed on the real keyboard, for whichever component has the focus.
/// The keyboard reports on the window's thread while the UI runs on the logic thread, so the
/// characters wait in a queue in between.
/// </summary>
internal static class UIKeyboard
{
    /// <summary>Stands for the backspace key in the queue.</summary>
    public const char BACKSPACE = '\b';

    /// <summary>Stands for either enter key in the queue.</summary>
    public const char ENTER = '\n';

    // Nobody reads the queue while nothing has the focus, so it has to stop growing by itself.
    private const int MAX_PENDING = 64;

    private static readonly ConcurrentQueue<char> typed = new();
    private static bool hooked;

    /// <summary>
    /// Starts listening to the keyboards. Only the first call does anything; has to be made once
    /// the window exists.
    /// </summary>
    public static void Hook()
    {
        if (hooked || GameEngine.Instance.InputManager.NativeInputContext is not { } input)
            return;

        hooked = true;

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyChar += (_, character) => Push(character);
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.Backspace)
                    Push(BACKSPACE);
                else if (key is Key.Enter or Key.KeypadEnter)
                    Push(ENTER);
            };
        }
    }

    /// <summary>Takes the next typed character, in the order they were typed.</summary>
    public static bool TryRead(out char character) => typed.TryDequeue(out character);

    /// <summary>Throws away whatever was typed while nothing was listening.</summary>
    public static void Clear() => typed.Clear();

    private static void Push(char character)
    {
        if (typed.Count < MAX_PENDING)
            typed.Enqueue(character);
    }
}
