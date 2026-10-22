using Silk.NET.GLFW;
using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// The keyboard of <see cref="GlfwInputWithoutGamepads"/>, as Silk's GLFW one has it. A key is heard going down and
/// coming up, not held (a text box repeats keys itself), and the clipboard is GLFW's.
/// </summary>
internal sealed unsafe class GlfwKeyboard : IKeyboard
{
    private static readonly Key[] keys = [.. Enum.GetValues<Key>().Where(key => key != Key.Unknown)];

    private readonly Silk.NET.GLFW.Glfw glfw;
    private readonly WindowHandle* window;

    // The scancodes that are down, only ever touched on the thread of the window
    private readonly List<int> scancodesDown = [];

    public GlfwKeyboard(Silk.NET.GLFW.Glfw glfw, WindowHandle* window)
    {
        this.glfw = glfw;
        this.window = window;
    }

    public string Name => "Keyboard (GLFW)";

    public int Index => 0;

    public bool IsConnected => true;

    public IReadOnlyList<Key> SupportedKeys => keys;

    public string ClipboardText
    {
        get => glfw.GetClipboardString(window) ?? string.Empty;
        set => glfw.SetClipboardString(window, value);
    }

    public event Action<IKeyboard, Key, int>? KeyDown;
    public event Action<IKeyboard, Key, int>? KeyUp;
    public event Action<IKeyboard, char>? KeyChar;

    public bool IsKeyPressed(Key key) => key != Key.Unknown && glfw.GetKey(window, ToGlfw(key)) == (int)InputAction.Press;

    public bool IsScancodePressed(int scancode) => scancodesDown.Contains(scancode);

    public void BeginInput() { }

    public void EndInput() { }

    /// <summary>
    /// Silk's keys are GLFW's by another name, the same numbers (a test holds them to it).
    /// </summary>
    internal static Key FromGlfw(Keys key) => (Key)(int)key;

    internal static Keys ToGlfw(Key key) => (Keys)(int)key;

    private bool Held(Keys left, Keys right) => glfw.GetKey(window, left) == (int)InputAction.Press || glfw.GetKey(window, right) == (int)InputAction.Press;

    internal void Hear(Keys key, int scancode, InputAction action)
    {
        switch (action)
        {
            case InputAction.Press:
                scancodesDown.Add(scancode);
                KeyDown?.Invoke(this, FromGlfw(key), scancode);
                break;
            case InputAction.Release:
                scancodesDown.Remove(scancode);
                KeyUp?.Invoke(this, FromGlfw(key), scancode);
                break;
        }
    }

    internal void HearChar(uint codepoint)
    {
        if (KeyChar is null) return;

        // Ctrl with a letter is a shortcut, never a letter. A real one comes as a control code GLFW drops already, a key
        // posted to the window (a script driving it) came through as the letter and Ctrl+A typed an a. Ctrl with Alt is
        // AltGr on Windows and types for real
        if (Held(Keys.ControlLeft, Keys.ControlRight) && !Held(Keys.AltLeft, Keys.AltRight))
            return;

        // Past the first plane (an emoji) it's two chars, which Silk's cuts down to one that means nothing
        if (codepoint > char.MaxValue && codepoint <= 0x10FFFF)
        {
            string pair = char.ConvertFromUtf32((int)codepoint);
            KeyChar(this, pair[0]);
            KeyChar(this, pair[1]);
        }
        else
        {
            KeyChar(this, (char)codepoint);
        }
    }
}
