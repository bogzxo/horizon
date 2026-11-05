using System.Numerics;
using System.Text;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Logging;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.Game;

/// <summary>
/// Everything that points and types, on one page that says what it heard. For trying a phone or a touch screen by hand,
/// and a keyboard and a mouse plugged into one. A button to tap, one to double click, one to hold for its menu, a slider,
/// a text box, and a list that scrolls by the wheel or a finger, with counters for all of it and a readout of the mouse,
/// the keys, the modifiers held and every finger on the screen. Every event is written to the log as well, with
/// "[TouchInput]" in front, so a run on a device can be checked after the fact (adb run-as ... cat files/debug.log).
/// </summary>
public class TouchInputExample : Scene, ITestControls
{
    private const string TAG = "[TouchInput]";
    private const double DOUBLE_CLICK = 0.4;
    private const int ROWS = 24;

    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Tap / click", "the buttons, a row, the slider"),
        new("Drag", "the list to scroll it, the slider to slide it"),
        new("Hold / right click", "the Hold button for its menu"),
        new("Wheel", "over the list"),
        new("Type", "into the box, Ctrl+A, Ctrl+C, Ctrl+V, Backspace"),
        new("Esc / Back", "counted, and lets go of the box"),
    ];

    private readonly UICompositor _compositor;
    private readonly UIModule _module;
    private readonly Label _counters, _mouse, _keys, _touch, _last;
    private readonly Slider _slider;
    private readonly TextBox _box;
    private readonly ScrollPanel _list;

    // What was heard, all of it simulation thread
    private int _taps, _doubleClicks, _menus, _rowClicks, _escapes, _texts, _keyDowns;
    private int _pointerDowns, _pointerUps, _pointerCancels, _mouseDowns;
    private float _wheel;
    private double _lastClickAt = -1.0;
    private int _clickRun;
    private string _lastRow = "none", _lastChord = "none", _lastEvent = "none";
    private readonly StringBuilder _typed = new();
    private Vector2 _dragFrom;
    private float _dragDistance;
    private bool _dragging;
    private readonly List<Key> _held = [];
    private double _time;

    public TouchInputExample()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        _compositor = AddComponent(new UICompositor(camera) { DesignSize = new Vector2(1000, 1200) });
        _module = _compositor.CreateModule();

        var page = _module.AddComponent(new StackPanel { Spacing = 10.0f, Size = new Vector2(960, 1160) });
        page.Add(new Label("Touch, mouse and keyboard") { Bold = true, TextScale = 1.3f });
        page.Add(new Label("Every finger, button, wheel turn and key is counted here and logged as [TouchInput].") { Dim = true });

        var columns = page.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 20.0f });
        var controls = columns.Add(new StackPanel { Spacing = 10.0f, Size = new Vector2(460, 0) });
        var readout = columns.Add(new StackPanel { Spacing = 8.0f, Size = new Vector2(480, 0) });

        var tap = controls.Add(new Button("Tap me") { Size = new Vector2(440, 64) });
        tap.OnPressed = () =>
        {
            _taps++;
            Note($"tap button clicked ({_taps})");
        };

        var twice = controls.Add(new Button("Double click me") { Size = new Vector2(440, 64) });
        twice.OnPressed = () =>
        {
            _clickRun = _time - _lastClickAt < DOUBLE_CLICK ? _clickRun + 1 : 1;
            _lastClickAt = _time;
            if (_clickRun == 2) _doubleClicks++;
            Note($"click {_clickRun} of a run, {_doubleClicks} double clicks");
        };

        var hold = controls.Add(new Button("Hold or right click me") { Size = new Vector2(440, 64), Tooltip = "Its menu is a counter" });
        hold.OnContextMenu = _ =>
        {
            _menus++;
            Note($"context menu asked for ({_menus}), by {_compositor.Pointer.Kind}");
        };

        _slider = controls.Add(new Slider { Size = new Vector2(440, 48), Min = 0, Max = 100, Value = 50 });
        _slider.OnChanged = value => Note($"slider {value:0}");

        _box = controls.Add(new TextBox { Size = new Vector2(440, 56) });
        _box.OnChanged = text => Note($"box says '{text}'");
        _box.OnSubmitted = text => Note($"box submitted '{text}'");

        var reset = controls.Add(new Button("Reset counters") { Size = new Vector2(440, 56) });
        reset.OnPressed = Reset;

        _list = controls.Add(new ScrollPanel { Size = new Vector2(440, 420) });
        var rows = _list.Add(new StackPanel { Spacing = 6.0f });
        for (int i = 1; i <= ROWS; i++)
        {
            int number = i;
            var row = rows.Add(new Button($"Row {number} of {ROWS}") { Size = new Vector2(400, 52) });
            row.OnPressed = () =>
            {
                _rowClicks++;
                _lastRow = $"row {number}";
                Note($"row {number} clicked ({_rowClicks})");
            };
        }

        _counters = readout.Add(new Label { Wrap = true, Size = new Vector2(480, 0), Align = Origin.TopLeft });
        _mouse = readout.Add(new Label { Wrap = true, Size = new Vector2(480, 0), Align = Origin.TopLeft });
        _keys = readout.Add(new Label { Wrap = true, Size = new Vector2(480, 0), Align = Origin.TopLeft });
        _touch = readout.Add(new Label { Wrap = true, Size = new Vector2(480, 0), Align = Origin.TopLeft });
        _last = readout.Add(new Label { Wrap = true, Size = new Vector2(480, 0), Align = Origin.TopLeft, Dim = true });

        Engine.Input.EventReceived += Heard;
        Log.Info($"{TAG} ready, design 1000x1200, window {Engine.WindowManager.ViewportSize.X:0}x{Engine.WindowManager.ViewportSize.Y:0}");
    }

    public override void PostInit()
    {
        base.PostInit();
        Engine.Graphics.ClearColor = new Vector4(0.11f, 0.12f, 0.16f, 1.0f);
    }

    // Every event the input heard, in order, simulation thread. Moves are only counted, there are hundreds a second
    private void Heard(InputEvent input)
    {
        switch (input.Kind)
        {
            case InputEventKind.PointerMove:
                if (input.PointerKind == PointerKind.Mouse && _dragging)
                    _dragDistance += Vector2.Distance(_dragFrom, input.Position);
                if (input.PointerKind == PointerKind.Mouse && _dragging)
                    _dragFrom = input.Position;
                return;

            case InputEventKind.PointerDown:
                _pointerDowns++;
                if (input.PointerKind == PointerKind.Mouse)
                {
                    _mouseDowns++;
                    if (input.Button == MouseButton.Left)
                    {
                        _dragging = true;
                        _dragFrom = input.Position;
                        _dragDistance = 0.0f;
                    }
                }
                break;

            case InputEventKind.PointerUp:
                _pointerUps++;
                if (input.PointerKind == PointerKind.Mouse && input.Button == MouseButton.Left) _dragging = false;
                break;

            case InputEventKind.PointerCancel:
                _pointerCancels++;
                _dragging = false;
                break;

            case InputEventKind.Scroll:
                _wheel += input.ScrollY;
                break;

            case InputEventKind.KeyDown:
                _keyDowns++;
                _lastChord = Chord(input.Modifiers, input.Key);
                if (input.Key == Key.Escape) _escapes++;
                break;

            case InputEventKind.Text:
                _texts++;
                _typed.Append(input.Text);
                if (_typed.Length > 40) _typed.Remove(0, _typed.Length - 40);
                break;

            case InputEventKind.KeyUp:
                break;

            default:
                return;
        }

        _lastEvent = Describe(input);
        Log.Info($"{TAG} {_lastEvent}");
    }

    private static string Describe(InputEvent input) => input.Kind switch
    {
        InputEventKind.KeyDown or InputEventKind.KeyUp => $"{input.Kind} {input.Key} mods={input.Modifiers}",
        InputEventKind.Text => $"Text '{input.Text}'",
        InputEventKind.Scroll => $"Scroll {input.ScrollY:0.##} at {input.Position.X:0},{input.Position.Y:0}",
        _ => $"{input.Kind} {input.PointerKind} id={input.PointerId} button={input.Button} at {input.Position.X:0},{input.Position.Y:0}"
    };

    private static string Chord(KeyModifiers modifiers, Key key)
    {
        var chord = new StringBuilder();
        if ((modifiers & KeyModifiers.Control) != 0) chord.Append("Ctrl+");
        if ((modifiers & KeyModifiers.Shift) != 0) chord.Append("Shift+");
        if ((modifiers & KeyModifiers.Alt) != 0) chord.Append("Alt+");
        if ((modifiers & KeyModifiers.Super) != 0) chord.Append("Super+");
        return chord.Append(key).ToString();
    }

    private void Note(string what)
    {
        _lastEvent = what;
        Log.Info($"{TAG} {what}");
    }

    private void Reset()
    {
        _taps = _doubleClicks = _menus = _rowClicks = _escapes = _texts = _keyDowns = 0;
        _pointerDowns = _pointerUps = _pointerCancels = _mouseDowns = 0;
        _wheel = 0.0f;
        _dragDistance = 0.0f;
        _typed.Clear();
        Note("counters reset");
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _time += dt;

        var input = Engine.Input;
        var mouse = input.Mouse;
        var keyboard = input.Keyboard;

        _counters.Text =
            $"taps {_taps}   double clicks {_doubleClicks}   menus {_menus}\n" +
            $"rows {_rowClicks} (last {_lastRow})   slider {_slider.Value:0}\n" +
            $"downs {_pointerDowns}   ups {_pointerUps}   cancels {_pointerCancels}\n" +
            $"list offset {_list.Offset:0} of {_list.MaxOffset:0}";

        _mouse.Text =
            $"mouse {mouse.Position.X:0},{mouse.Position.Y:0}   buttons {Buttons(mouse)}\n" +
            $"mouse downs {_mouseDowns}   wheel {_wheel:0.##}   drag {_dragDistance:0} px{(_dragging ? " (held)" : "")}";

        keyboard.CopyDown(_held);
        _keys.Text =
            $"held {(_held.Count == 0 ? "nothing" : string.Join(' ', _held))}\n" +
            $"mods {(keyboard.Control ? "Ctrl " : "")}{(keyboard.Shift ? "Shift " : "")}{(keyboard.Alt ? "Alt " : "")}{(keyboard.IsDown(Key.SuperLeft) || keyboard.IsDown(Key.SuperRight) ? "Super" : "")}\n" +
            $"last chord {_lastChord}   key downs {_keyDowns}   esc/back {_escapes}\n" +
            $"text events {_texts}   typed '{_typed}'\n" +
            $"box '{_box.Text}'{(_compositor.Focus == _box ? " (focused)" : "")}   soft keyboard {(input.TextInputActive ? "asked for" : "not asked")}";

        var fingers = new StringBuilder();
        foreach (TouchPoint point in input.Touch.Points)
            fingers.Append($"#{point.Id} {point.Kind} {point.Position.X:0},{point.Position.Y:0}{(point.WasCancelled ? " cancelled" : point.WasReleased ? " up" : "")}  ");

        _touch.Text =
            $"pointer {_compositor.Pointer.Kind}{(_compositor.Pointer.Down ? " down" : "")}   last used {input.LastPointerKind}{(_compositor.IsTouchPanning ? "   scrolling" : "")}\n" +
            $"fingers {input.Touch.Count}  {fingers}";

        _last.Text = $"last {_lastEvent}";
    }

    private static string Buttons(Mouse mouse)
    {
        var held = new StringBuilder();
        if (mouse.IsDown(MouseButton.Left)) held.Append("L ");
        if (mouse.IsDown(MouseButton.Right)) held.Append("R ");
        if (mouse.IsDown(MouseButton.Middle)) held.Append("M ");
        if (mouse.IsDown(MouseButton.Button4)) held.Append("Back ");
        if (mouse.IsDown(MouseButton.Button5)) held.Append("Fwd ");
        return held.Length == 0 ? "none" : held.ToString().TrimEnd();
    }

    protected override void DisposeOther()
    {
        Engine.Input.EventReceived -= Heard;
        base.DisposeOther();
    }
}
