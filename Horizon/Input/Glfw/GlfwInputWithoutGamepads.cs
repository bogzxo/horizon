using Silk.NET.GLFW;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace Horizon.Input;

/// <summary>
/// The input context of a GLFW window with a keyboard and a mouse and nothing else, for an engine told it has no use for
/// gamepads (<see cref="Engine.GameEngineConfiguration.Gamepads"/>). Silk's own asks GLFW about sixteen gamepads and
/// sixteen joysticks the moment it is made, and again every time the window hears from the system. The first time, GLFW
/// goes through every game controller DirectInput knows of, and a broken controller driver turns that into a window that
/// takes a minute to come up. This one never asks, so GLFW never goes looking.
/// </summary>
internal sealed unsafe class GlfwInputWithoutGamepads : IInputContext
{
    private readonly Silk.NET.GLFW.Glfw glfw;
    private readonly WindowHandle* window;

    private readonly GlfwKeyboard keyboard;
    private readonly GlfwMouse mouse;

    // Kept for as long as GLFW holds on to them, it calling a delegate that has been collected is a crash
    private readonly GlfwCallbacks.KeyCallback onKey;
    private readonly GlfwCallbacks.CharCallback onChar;
    private readonly GlfwCallbacks.MouseButtonCallback onButton;
    private readonly GlfwCallbacks.CursorPosCallback onMove;
    private readonly GlfwCallbacks.ScrollCallback onScroll;

    // What GLFW says went wrong, written down rather than thrown. Silk's own throws from inside GLFW's call, where no
    // catch reaches, and a clipboard it couldn't read took a published Hex down with it. Kept for as long as GLFW is
    private static readonly GlfwCallbacks.ErrorCallback onError = (code, description) => Logging.Log.Warning($"[GLFW] {code}, {description}");

    public GlfwInputWithoutGamepads(IView view)
    {
        glfw = GlfwWindowing.GetExistingApi(view) ?? throw new InvalidOperationException("Only a GLFW window has an input context without gamepads.");
        window = GlfwWindowing.GetHandle(view);
        Handle = (nint)window;

        glfw.SetErrorCallback(onError);
        keyboard = new GlfwKeyboard(glfw, window);
        mouse = new GlfwMouse(glfw, window);
        Keyboards = [keyboard];
        Mice = [mouse];

        // Called on the thread of the window, while it polls for events
        glfw.SetKeyCallback(window, onKey = (_, key, scancode, action, _) => keyboard.Hear(key, scancode, action));
        glfw.SetCharCallback(window, onChar = (_, codepoint) => keyboard.HearChar(codepoint));
        glfw.SetMouseButtonCallback(window, onButton = (_, button, action, _) => mouse.HearButton(button, action));
        glfw.SetCursorPosCallback(window, onMove = (_, x, y) => mouse.HearMove(x, y));
        glfw.SetScrollCallback(window, onScroll = (_, x, y) => mouse.HearScroll(x, y));
    }

    public nint Handle { get; }

    public IReadOnlyList<IGamepad> Gamepads { get; } = [];

    public IReadOnlyList<IJoystick> Joysticks { get; } = [];

    public IReadOnlyList<IKeyboard> Keyboards { get; }

    public IReadOnlyList<IMouse> Mice { get; }

    public IReadOnlyList<IInputDevice> OtherDevices { get; } = [];

    // The keyboard and the mouse are there for as long as the window is, nothing comes or goes
    public event Action<IInputDevice, bool>? ConnectionChanged { add { } remove { } }

    public void Dispose()
    {
        glfw.SetKeyCallback(window, null);
        glfw.SetCharCallback(window, null);
        glfw.SetMouseButtonCallback(window, null);
        glfw.SetCursorPosCallback(window, null);
        glfw.SetScrollCallback(window, null);
        mouse.Dispose();
    }
}
