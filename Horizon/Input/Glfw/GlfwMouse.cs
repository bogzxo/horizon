using System.Numerics;

using Silk.NET.GLFW;
using Silk.NET.Input;

using GlfwButton = Silk.NET.GLFW.MouseButton;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Horizon.Input;

/// <summary>
/// The mouse of <see cref="GlfwInputWithoutGamepads"/>. Its buttons, where it is, the wheel and the cursor, as Silk's
/// GLFW one has them. A click is a button let go of where it went down, and a double click the second press of a
/// button soon enough after the first and near enough to it.
/// </summary>
internal sealed unsafe class GlfwMouse : IMouse, IDisposable
{
    // GLFW numbers eight buttons from 0, and so does Silk, from Left to Button8
    private const int BUTTONS = 8;

    private static readonly MouseButton[] buttons = [.. Enum.GetValues<MouseButton>().Where(button => button is >= MouseButton.Left and <= MouseButton.Button8)];

    private readonly Silk.NET.GLFW.Glfw glfw;
    private readonly WindowHandle* window;
    private readonly GlfwCursor cursor;
    private readonly ScrollWheel[] wheels = new ScrollWheel[1];

    // The button that went down last, when and where, for telling a double click from two clicks
    private MouseButton lastDown = MouseButton.Unknown;
    private long lastDownAt;
    private Vector2 lastDownWhere;

    public GlfwMouse(Silk.NET.GLFW.Glfw glfw, WindowHandle* window)
    {
        this.glfw = glfw;
        this.window = window;
        cursor = new GlfwCursor(glfw, window);
    }

    public string Name => "Mouse (GLFW)";

    public int Index => 0;

    public bool IsConnected => true;

    public IReadOnlyList<MouseButton> SupportedButtons => buttons;

    public IReadOnlyList<ScrollWheel> ScrollWheels => wheels;

    public Vector2 Position
    {
        get
        {
            glfw.GetCursorPos(window, out double x, out double y);
            return new Vector2((float)x, (float)y);
        }
        set => glfw.SetCursorPos(window, value.X, value.Y);
    }

    public ICursor Cursor => cursor;

    public int DoubleClickTime { get; set; } = 500;

    public int DoubleClickRange { get; set; } = 4;

    public event Action<IMouse, MouseButton>? MouseDown;
    public event Action<IMouse, MouseButton>? MouseUp;
    public event Action<IMouse, MouseButton, Vector2>? Click;
    public event Action<IMouse, MouseButton, Vector2>? DoubleClick;
    public event Action<IMouse, Vector2>? MouseMove;
    public event Action<IMouse, ScrollWheel>? Scroll;

    public bool IsButtonPressed(MouseButton button) =>
        (uint)button < BUTTONS && glfw.GetMouseButton(window, (int)button) == (int)InputAction.Press;

    internal void HearButton(GlfwButton glfwButton, InputAction action)
    {
        var button = (int)glfwButton is >= 0 and < BUTTONS ? (MouseButton)(int)glfwButton : MouseButton.Unknown;
        Vector2 where = Position;

        if (action == InputAction.Press)
        {
            MouseDown?.Invoke(this, button);

            long now = Environment.TickCount64;
            bool twice = button == lastDown && now - lastDownAt <= DoubleClickTime
                && MathF.Abs(where.X - lastDownWhere.X) < DoubleClickRange && MathF.Abs(where.Y - lastDownWhere.Y) < DoubleClickRange;

            if (twice)
            {
                // A third press starts over rather than being a second double click
                lastDown = MouseButton.Unknown;
                DoubleClick?.Invoke(this, button, where);
            }
            else
            {
                lastDown = button;
                lastDownAt = now;
                lastDownWhere = where;
            }
        }
        else if (action == InputAction.Release)
        {
            MouseUp?.Invoke(this, button);
            if (MathF.Abs(where.X - lastDownWhere.X) < DoubleClickRange && MathF.Abs(where.Y - lastDownWhere.Y) < DoubleClickRange)
                Click?.Invoke(this, button, where);
        }
    }

    internal void HearMove(double x, double y) => MouseMove?.Invoke(this, new Vector2((float)x, (float)y));

    internal void HearScroll(double x, double y)
    {
        wheels[0] = new ScrollWheel((float)x, (float)y);
        Scroll?.Invoke(this, wheels[0]);
    }

    public void Dispose() => cursor.Dispose();
}
