using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Somewhere a program draws, boxes, lines, text and the frames of a <see cref="UISprite"/>, kept until it draws again
/// (nobody is asked every frame what to draw, a script least of all). In units of its own, the middle of the drawing at
/// 0 0 and up being up, shown <see cref="Zoom"/> times as big and moved by <see cref="Pan"/>, the wheel zooming round the
/// pointer and the middle button (or the right one) dragging it about unless it is told not to. What the pointer does
/// in it comes back in the same units (<see cref="OnPointer"/>), and the keys while it has them (<see cref="OnKey"/>).
/// <code>
/// let canvas = compositor.drawing({ size: vec(600, 400) });
/// canvas.clear();
/// canvas.sprite(art, "jab", 3, vec(-64, -64), vec(64, 64));
/// canvas.frame(vec(0, 0), vec(20, 30), 2, vec(1, 0.2, 0.2, 1));
/// canvas.on_pointer = func(e) { print(e.kind, e.at); };
/// </code>
/// </summary>
public class DrawingPanel : UIComponent
{
    private enum Kind : byte { Rect, Frame, Line, Text, Sprite }

    private readonly record struct Shape(Kind Kind, Vector2 A, Vector2 B, Vector4 Color, float Size, string? Text, UISprite? Sprite, string? Animation, int Frame, bool Mirrored);

    private readonly List<Shape> shapes = [];
    private IRuntimeValue? pointerHandler, keyHandler;

    private bool panning;
    private Vector2 panFrom, panStart;
    private Vector2 lastPointer;
    private bool pointerDown;

    /// <summary>How many units on screen a unit of the drawing is.</summary>
    public float Zoom { get; set; } = 1.0f;

    /// <summary>Where the middle of the drawing is from the middle of the component, in units on screen.</summary>
    public Vector2 Pan { get; set; }

    /// <summary>Whether the wheel zooms and the middle and right buttons pan by themselves.</summary>
    public bool Pannable { get; set; } = true;

    /// <summary>The colour behind it, null for the skin's field colour, see-through with a W of 0.</summary>
    public Vector4? Color { get; set; }

    /// <summary>What the pointer does in it, where (in its units), down, drag, up or move, and which modifiers.</summary>
    public Action<DrawingPointer>? OnPointer { get; set; }

    /// <summary>A key while it has the keyboard, by name (left, right, up, down, home, end, delete, enter, escape, space, or the letter).</summary>
    public Action<string>? OnKey { get; set; }

    protected override bool HitTestVisible => true;

    protected internal override bool Focusable => true;

    /* Drawing */

    /// <summary>How many shapes it holds, and how many of them are frames of a sprite.</summary>
    public int ShapeCount => shapes.Count;

    public int SpriteCount => shapes.Count(shape => shape.Kind == Kind.Sprite);

    public void Clear() => shapes.Clear();

    public void Rect(Vector2 min, Vector2 max, Vector4 color) => shapes.Add(new Shape(Kind.Rect, min, max, color, 0, null, null, null, 0, false));

    public void Frame(Vector2 min, Vector2 max, float thickness, Vector4 color) => shapes.Add(new Shape(Kind.Frame, min, max, color, thickness, null, null, null, 0, false));

    public void Line(Vector2 from, Vector2 to, float thickness, Vector4 color) => shapes.Add(new Shape(Kind.Line, from, to, color, thickness, null, null, null, 0, false));

    public void Text(string text, Vector2 at, float scale, Vector4 color) => shapes.Add(new Shape(Kind.Text, at, at, color, scale, text, null, null, 0, false));

    public void Sprite(UISprite sprite, string animation, int frame, Vector2 min, Vector2 max, Vector4 tint, bool mirrored = false) =>
        shapes.Add(new Shape(Kind.Sprite, min, max, tint, 0, null, sprite, animation, frame, mirrored));

    /// <summary>Zooms and pans so a part of the drawing fills it, with a margin of so many units on screen.</summary>
    public void Fit(Vector2 min, Vector2 max, float margin = 24.0f)
    {
        UIRect bounds = Bounds;
        if (bounds.IsEmpty) return;

        Vector2 size = Vector2.Max(max - min, Vector2.One);
        Zoom = MathF.Max(0.01f, MathF.Min((bounds.Width - margin * 2.0f) / size.X, (bounds.Height - margin * 2.0f) / size.Y));
        Pan = -(min + max) * 0.5f * Zoom;
    }

    /// <summary>A point of the drawing on screen (the module's units).</summary>
    public Vector2 ToScreen(Vector2 at) => Bounds.Center + Pan + at * Zoom;

    /// <summary>A point on screen as a point of the drawing.</summary>
    public Vector2 FromScreen(Vector2 at) => (at - Bounds.Center - Pan) / Zoom;

    protected override Vector2 Measure(UISkin skin) => new(400.0f, 300.0f);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect bounds = Bounds;
        Vector4 behind = Color ?? skin.FieldColor;
        if (behind.W > 0.0f)
            list.Box(bounds, behind);

        list.PushClip(bounds);
        for (int i = 0; i < shapes.Count; i++)
        {
            Shape shape = shapes[i];
            Vector2 a = ToScreen(shape.A), b = ToScreen(shape.B);
            UIRect box = new(Vector2.Min(a, b), Vector2.Max(a, b));

            switch (shape.Kind)
            {
                case Kind.Rect:
                    list.Rect(box, shape.Color);
                    break;

                case Kind.Frame:
                    list.Outline(box, shape.Size, shape.Color);
                    break;

                case Kind.Line:
                    Line(list, a, b, shape.Size, shape.Color);
                    break;

                case Kind.Text:
                    list.Text(shape.Text, a, shape.Size > 0.0f ? shape.Size : skin.TextScale, shape.Color, markup: false);
                    break;

                case Kind.Sprite when shape.Sprite!.Key(shape.Animation!, shape.Frame) is { } key:
                    list.Image(shape.Sprite.Atlas, key, box, shape.Color, shape.Mirrored);
                    break;
            }
        }

        list.PopClip();
    }

    /// <summary>A line as a thin box turned along it, the draw list has no lines of its own, so as steps for a slanted one.</summary>
    private static void Line(UIDrawList list, Vector2 a, Vector2 b, float thickness, Vector4 color)
    {
        float half = MathF.Max(0.5f, thickness * 0.5f);
        if (MathF.Abs(a.X - b.X) < 0.5f || MathF.Abs(a.Y - b.Y) < 0.5f)
        {
            list.Rect(new UIRect(Vector2.Min(a, b) - new Vector2(half), Vector2.Max(a, b) + new Vector2(half)), color);
            return;
        }

        int steps = (int)Math.Clamp(Vector2.Distance(a, b) / 2.0f, 2.0f, 256.0f);
        for (int i = 0; i < steps; i++)
        {
            Vector2 from = Vector2.Lerp(a, b, i / (float)steps), to = Vector2.Lerp(a, b, (i + 1) / (float)steps);
            list.Rect(new UIRect(Vector2.Min(from, to) - new Vector2(half), Vector2.Max(from, to) + new Vector2(half)), color);
        }
    }

    /* The pointer and the keys */

    protected internal override void OnPointerDown(Vector2 point)
    {
        pointerDown = true;
        Raise("down", point);
    }

    protected internal override void OnPointerDrag(Vector2 point) => Raise("drag", point);

    protected internal override void OnPointerUp(Vector2 point)
    {
        pointerDown = false;
        Raise("up", point);
    }

    protected internal override bool OnScroll(float delta)
    {
        if (!Pannable || Module is not { } module)
            return false;

        // Round the pointer, what is under it stays under it
        Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
        Vector2 under = FromScreen(pointer);
        Zoom = Math.Clamp(Zoom * (delta > 0 ? 1.15f : 1.0f / 1.15f), 0.05f, 64.0f);
        Pan += pointer - ToScreen(under);
        return true;
    }

    protected override void Update(float dt)
    {
        if (Module is not { } module)
            return;

        Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);

        // The middle or the right button drags the drawing about
        var mouse = Horizon.Engine.GameEngine.Instance?.Input.Mouse;
        bool panHeld = mouse is not null && (mouse.IsDown(Silk.NET.Input.MouseButton.Middle) || mouse.IsDown(Silk.NET.Input.MouseButton.Right));
        if (Pannable && panHeld && (panning || (IsHovered && Bounds.Contains(pointer))))
        {
            if (!panning)
            {
                panning = true;
                panFrom = pointer;
                panStart = Pan;
            }

            Pan = panStart + pointer - panFrom;
        }
        else
        {
            panning = false;
        }

        // The pointer moving over it with nothing held, for a hover
        if (!pointerDown && IsHovered && pointer != lastPointer && Bounds.Contains(pointer))
            Raise("move", pointer);
        lastPointer = pointer;
    }

    private void Raise(string kind, Vector2 point)
    {
        var keyboard = Horizon.Engine.GameEngine.Instance?.Input.Keyboard;
        var happened = new DrawingPointer(kind, FromScreen(point), keyboard?.Shift == true, keyboard?.Control == true);
        OnPointer?.Invoke(happened);

        if (pointerHandler is not null)
        {
            InvokeScript(pointerHandler, Natives.Object(
                ("kind", new StringValue(happened.Kind)),
                ("at", new Vector2Value(happened.At)),
                ("shift", Values.Bool(happened.Shift)),
                ("control", Values.Bool(happened.Control))));
        }
    }

    protected internal override void OnTextInput(char character)
    {
        string key = character switch
        {
            UIKeyboard.LEFT => "left",
            UIKeyboard.RIGHT => "right",
            UIKeyboard.UP => "up",
            UIKeyboard.DOWN => "down",
            UIKeyboard.HOME => "home",
            UIKeyboard.END => "end",
            UIKeyboard.DELETE => "delete",
            UIKeyboard.BACKSPACE => "backspace",
            UIKeyboard.ENTER => "enter",
            UIKeyboard.ESCAPE => "escape",
            UIKeyboard.PAGE_UP => "page_up",
            UIKeyboard.PAGE_DOWN => "page_down",
            ' ' => "space",
            _ when !char.IsControl(character) && character < '' => character.ToString(),
            _ => string.Empty
        };

        if (key.Length == 0)
            return;

        OnKey?.Invoke(key);
        InvokeScript(keyHandler, new StringValue(key));
    }

    /* Scripts */

    private static Vector4 ColourOf(Arguments args, int at) => args.Has(at) ? args.Vector4(at) : Vector4.One;

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("zoom", () => Zoom, value => Zoom = MathF.Max(0.01f, value));
        Expose("pan", () => new Vector2Value(Pan), value => Pan = value is Vector2Value vector ? vector.Value : Pan);
        Expose("navigable", () => Pannable, value => Pannable = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("on_pointer", () => pointerHandler ?? new NullValue(), value => pointerHandler = value);
        Expose("on_key", () => keyHandler ?? new NullValue(), value => keyHandler = value);

        // The drawing itself, functions a script calls on it
        Method("clear", _ => Clear());
        Method("rect", args => Rect(args.Vector2(0), args.Vector2(1), ColourOf(args, 2)));
        Method("frame", args => Frame(args.Vector2(0), args.Vector2(1), args.Number(2, 1.0f), ColourOf(args, 3)));
        Method("line", args => Line(args.Vector2(0), args.Vector2(1), args.Number(2, 1.0f), ColourOf(args, 3)));
        Method("text", args => Text(args.Text(0), args.Vector2(1), args.Number(2, 0.0f), ColourOf(args, 3)));
        Method("sprite", args =>
        {
            if (args[0] is not UISprite sprite)
                throw args.Wrong("the first argument has to be a sprite, hex.sprite(path).");
            Sprite(sprite, args.Text(1), args.Integer(2, 0), args.Vector2(3), args.Vector2(4), ColourOf(args, 5), args.Bool(6, false));
        });
        Method("fit", args => Fit(args.Vector2(0), args.Vector2(1), args.Number(2, 24.0f)));
        Expose("view_size", () => new Vector2Value(Bounds.Size / MathF.Max(0.01f, Zoom)), _ => { });
    }

    /// <summary>Helper method to put a function of the drawing on its script object, called like a method.</summary>
    private void Method(string name, Action<Arguments> body)
    {
        NativeFunctionValue function = Natives.Action(name, body);
        Expose(name, () => function, _ => { });
    }
}

/// <summary>What the pointer did in a <see cref="DrawingPanel"/>, where in the drawing's units, and with which modifiers.</summary>
public readonly record struct DrawingPointer(string Kind, Vector2 At, bool Shift, bool Control);
