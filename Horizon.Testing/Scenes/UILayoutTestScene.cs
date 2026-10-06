using System;
using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

using Button = Horizon.Rendering.UIX.Components.Button;

namespace Horizon.Testing.Scenes;

/// <summary>
/// The parts of the UI library that are about laying things out: scaling a whole UI to its window, grids,
/// scrolling, the number box and the selector, and UIs that are loaded from layout files with their dynamic
/// items made from templates.
/// With a self-test a scripted pointer works its way through all of it and the results are printed.
/// </summary>
public class UILayoutTestScene : Scene, ITestControls
{
    private const int ScrollRows = 12;

    // The screen the test is laid out for. The window of the tests is that size, so the UI starts at a scale of 1.
    private static readonly Vector2 DesignSize = new(1600, 900);

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Up / Down", "scale the whole UI"),
        new("Mouse", "press, drag, scroll")
    ];

    private readonly UICompositor _compositor;
    private readonly UISelfTest? _selfTest;

    private UIModule _built = null!, _loaded = null!;
    private GridPanel _grid = null!;
    private ScrollPanel _scroll = null!;
    private NumberBox _number = null!;
    private Selector _selector = null!;
    private Label _status = null!;
    private UILayout _layout = null!;
    private IReadOnlyList<UILayout> _items = [];

    private readonly Button[] _cells = new Button[5];
    private readonly int[] _cellPresses = new int[5];
    private readonly Button[] _rows = new Button[ScrollRows];
    private readonly int[] _rowPresses = new int[ScrollRows];

    private int _numberChanges, _goPresses;
    private int _itemPressed = -1;
    private string _layoutDirectory = string.Empty;

    public UILayoutTestScene(bool selfTest = false)
    {
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;

        _compositor = AddComponent(new UICompositor(cam) { DesignSize = DesignSize });

        if (selfTest)
        {
            _selfTest = new UISelfTest();
            _compositor.PointerSource = () => _selfTest.Pointer;
        }
    }

    public override void PostInit()
    {
        base.PostInit();

        BuildControls();
        BuildFromFiles();

        if (_selfTest is not null)
            ScriptSelfTest(_selfTest);

        Engine.GL.ClearColor(0.22f, 0.27f, 0.36f, 1.0f);
    }

    // 1. A grid, something to scroll, a number box and a selector, put together in C#.
    private void BuildControls()
    {
        _built = _compositor.CreateModule();
        _built.Position = new Vector2(-380, 0);

        var panel = _built.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(20),
            Spacing = 12
        });

        panel.Add(new Label("Grid, scrolling, numbers") { TextScale = 0.3f });

        _grid = panel.Add(new GridPanel { Columns = 3, Spacing = 6 });
        for (int i = 0; i < _cells.Length; i++)
        {
            int index = i;
            _cells[i] = _grid.Add(new Button($"{i + 1}")
            {
                Style = "button_flat",
                Size = new Vector2(96, 40),
                LabelScale = 0.24f,
                OnPressed = () => _cellPresses[index]++
            });
        }

        // More rows than fit, which is the point.
        _scroll = panel.Add(new ScrollPanel
        {
            Size = new Vector2(300, 150),
            Color = new Vector4(0.03f, 0.03f, 0.05f, 1.0f),
            Padding = new UIEdges(4)
        });
        var rows = _scroll.Add(new StackPanel { Spacing = 4 });
        for (int i = 0; i < ScrollRows; i++)
        {
            int index = i;
            _rows[i] = rows.Add(new Button($"row {i + 1}")
            {
                Style = "button_flat",
                Size = new Vector2(270, 32),
                LabelScale = 0.2f,
                OnPressed = () => _rowPresses[index]++
            });
        }

        _number = panel.Add(new NumberBox(10) { Size = new Vector2(300, 38), TextScale = 0.24f });
        _number.OnValueChanged = _ => _numberChanges++;

        _selector = panel.Add(new Selector("one", "two", "three") { Size = new Vector2(300, 38), TextScale = 0.24f });

        _status = _built.AddComponent(new Label
        {
            Anchor = Origin.Bottom,
            Position = new Vector2(380, 70),
            TextScale = 0.25f
        });
    }

    // 2. A UI out of layout files: one for the panel, one for the items its list is filled with.
    private void BuildFromFiles()
    {
        _layoutDirectory = Path.Combine(Path.GetTempPath(), "horizon-ui-layout-test");
        Directory.CreateDirectory(_layoutDirectory);

        File.WriteAllText(Path.Combine(_layoutDirectory, "menu.hor"), """
            let box = compositor.stack({
                color: vec(0.1, 0.12, 0.17, 0.92),
                padding: 20,
                spacing: 10
            });
            let heading = compositor.label({ parent: box, text: "Out of a layout file", text_scale: 0.3 });
            let list = compositor.stack({ parent: box, spacing: 4, template: "item.hor", preview_count: 2 });
            let go = compositor.button({ parent: box, label: "Go", style: "button_flat", size: vec(160, 40), lbl_scale: 0.24 });
            """);

        File.WriteAllText(Path.Combine(_layoutDirectory, "item.hor"), """
            let row = compositor.button({ style: "button_flat", size: vec(260, 36) });
            let caption = compositor.label({ parent: row, text: "item", text_scale: 0.2 });
            """);

        File.WriteAllText(Path.Combine(_layoutDirectory, "broken.hor"), """
            let fine = compositor.label({ text: "this one is fine" });
            let wrong = compositor.label({ no_such_property: 1 });
            """);

        _loaded = _compositor.CreateModule();
        _loaded.Position = new Vector2(380, 0);

        _layout = _loaded.LoadLayout(Path.Combine(_layoutDirectory, "menu.hor"));
        _layout.Get<Button>("go").OnPressed = () => _goPresses++;

        FillList(3);
    }

    private void FillList(int count)
    {
        // What a program does with a container that has a template: make the items, then fill them in.
        _items = _layout.Populate("list", count);

        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            _items[i].Get<Label>("caption").Text = $"item {i + 1} of {count}";
            _items[i].Get<Button>("row").OnPressed = () => _itemPressed = index;
        }
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _selfTest?.Update(dt);

        _status.Text = $"UI scale {_compositor.UIScale:0.00}, laid out for {_built.Root.Bounds.Width:0} x {_built.Root.Bounds.Height:0}";

        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.Up))
            _compositor.Scale = MathF.Min(2.0f, _compositor.Scale + 0.25f);
        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.Down))
            _compositor.Scale = MathF.Max(0.5f, _compositor.Scale - 0.25f);
    }

    private void ScriptSelfTest(UISelfTest test)
    {
        Func<Vector2> At(UIComponent component, float x = 0.5f, float y = 0.5f) => () =>
        {
            UIRect bounds = component.Bounds;
            return component.Module!.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Min.Y + bounds.Height * y));
        };

        Func<Vector2> away = () => new Vector2(0, -400);
        bool Near(float a, float b, float within = 0.5f) => MathF.Abs(a - b) <= within;

        // Something has to have been laid out before there is anything to check.
        test.Hover(away, 0.3f);

        /* The grid */

        test.Check("grid: cells follow each other to the right", () =>
            Near(_cells[1].Bounds.Min.X, _cells[0].Bounds.Max.X + 6) && Near(_cells[1].Bounds.Min.Y, _cells[0].Bounds.Min.Y));
        test.Check("grid: the fourth cell starts a row of its own under the first", () =>
            Near(_cells[3].Bounds.Min.X, _cells[0].Bounds.Min.X) && Near(_cells[3].Bounds.Max.Y, _cells[0].Bounds.Min.Y - 6));
        test.Check("grid: as big as three cells by two with the gaps", () =>
            Near(_grid.Bounds.Width, 3 * 96 + 2 * 6) && Near(_grid.Bounds.Height, 2 * 40 + 6));

        test.Click(At(_cells[4]));
        test.Check("grid: a cell is pressed where it is", () => _cellPresses[4] == 1 && _cellPresses[3] == 0);

        /* Scaling the whole UI */

        test.Check("scale: a window the size of the design is a scale of 1", () =>
            Near(_compositor.UIScale, 1.0f, 0.001f) && Near(_built.Root.Bounds.Width, DesignSize.X) && Near(_built.Root.Bounds.Height, DesignSize.Y));

        test.Run(() => _compositor.Scale = 1.5f);
        test.Hover(away, 0.3f);
        test.Check("scale: bigger means less room to lay out in", () =>
            Near(_compositor.UIScale, 1.5f, 0.001f) && Near(_built.Root.Bounds.Width, DesignSize.X / 1.5f, 1.0f));
        test.Check("scale: the layout itself doesn't change", () => Near(_cells[0].Bounds.Width, 96) && Near(_grid.Bounds.Width, 3 * 96 + 2 * 6));
        test.Check("scale: a point of the layout and back", () =>
        {
            Vector2 point = new(123, -45);
            return Vector2.Distance(_built.ToLocal(_built.ToWorld(point)), point) < 0.01f
                && Vector2.Distance(_built.ToWorld(Vector2.Zero), _built.Position * 1.5f) < 0.01f;
        });

        test.Click(At(_cells[1]));
        test.Check("scale: a UI drawn bigger is hit where it is drawn", () => _cellPresses[1] == 1);

        test.Run(() => _compositor.Scale = 0.75f);
        test.Hover(away, 0.3f);
        test.Click(At(_cells[2]));
        test.Check("scale: and one drawn smaller", () => _cellPresses[2] == 1 && Near(_compositor.UIScale, 0.75f, 0.001f));

        // A design half the size of the window: everything twice as big, and exactly the design's room.
        test.Run(() =>
        {
            _compositor.Scale = 1.0f;
            _compositor.DesignSize = DesignSize / 2.0f;
        });
        test.Hover(away, 0.3f);
        test.Check("scale: a design half the window's size is drawn twice as big", () =>
            Near(_compositor.UIScale, 2.0f, 0.001f) && Near(_built.Root.Bounds.Width, 800) && Near(_built.Root.Bounds.Height, 450));

        test.Run(() => _compositor.DesignSize = DesignSize);
        test.Hover(away, 0.3f);

        /* Scrolling */

        test.Check("scroll: there is more than fits", () => _scroll.MaxOffset > 100 && _scroll.Offset == 0);

        test.Click(At(_rows[0]));
        test.Check("scroll: a row that is in view is pressed", () => _rowPresses[0] == 1);

        // The last row is laid out below the panel, where it is cut off: there is nothing there to press.
        test.Click(At(_rows[ScrollRows - 1]));
        test.Check("scroll: a row that is cut off isn't there for the pointer", () => _rowPresses[ScrollRows - 1] == 0);

        test.Hover(At(_scroll, 0.4f), 0.2f);
        test.Run(() => _compositor.Scroll(-2));
        test.Hover(At(_scroll, 0.4f), 0.3f);
        test.Check("scroll: the wheel moves what is under the pointer", () => Near(_scroll.Offset, 2 * _scroll.WheelStep));

        test.Hover(away, 0.2f);
        test.Run(() => _compositor.Scroll(-2));
        test.Hover(away, 0.3f);
        test.Check("scroll: and nothing when the pointer is somewhere else", () => Near(_scroll.Offset, 2 * _scroll.WheelStep));

        // Dragging the bar all the way down. It is along the right edge, inside of the padding.
        Func<Vector2> BarAt(float fromTop) => () =>
        {
            UIRect content = _scroll.Bounds.Shrink(_scroll.Padding);
            return _built.ToWorld(new Vector2(content.Max.X - 3, content.Max.Y - content.Height * fromTop));
        };
        test.Drag(BarAt(0.3f), BarAt(1.2f));
        test.Check("scroll: dragging the bar to the bottom shows the end", () => Near(_scroll.Offset, _scroll.MaxOffset, 1.0f));

        test.Click(At(_rows[ScrollRows - 1]));
        test.Check("scroll: the last row can be pressed now", () => _rowPresses[ScrollRows - 1] == 1);
        test.Check("scroll: and the first one is the one that is cut off", () => _rows[0].Bounds.Min.Y > _scroll.Bounds.Max.Y - 1);

        /* The number box and the selector */

        test.Drag(At(_number, 0.2f), At(_number, 0.9f));
        test.Check("number box: dragging across it changes the number", () => _number.Value > 10 && _numberChanges > 0);
        test.Check("number box: and shows it", () => float.TryParse(_number.Text, System.Globalization.CultureInfo.InvariantCulture, out float shown) && Near(shown, _number.Value, 0.01f));

        test.Click(At(_selector, 0.9f));
        test.Check("selector: the right half moves on", () => _selector.Value == "two");
        test.Click(At(_selector, 0.1f));
        test.Click(At(_selector, 0.1f));
        test.Check("selector: the left half goes back, around the end", () => _selector.Value == "three");

        /* Layout files */

        test.Check("layout: its parts are there by name", () =>
            _layout.Parts.Count == 4 && _layout.TryGet<StackPanel>("list", out _) && _layout.Roots.Count == 1 && _layout.Roots[0] == _layout.Get<StackPanel>("box"));
        test.Check("layout: a container is filled from its template", () =>
            _layout.Get<StackPanel>("list").Children.Count == 3 && _items.Count == 3);
        test.Check("layout: every item has parts of its own under the same names", () =>
            _items[0].Get<Label>("caption") != _items[1].Get<Label>("caption")
            && _items[2].Get<Label>("caption").Text == "item 3 of 3"
            && _items[1].Get<Button>("row").Parent == _layout.Get<StackPanel>("list"));
        test.Check("layout: the names of an item don't leak into the module", () =>
            _loaded.Runtime.UserScope.Lookup("row") is Horizon.HIDL.Runtime.NullValue && !_layout.Parts.ContainsKey("row"));

        test.Click(At(_items[1].Get<Button>("row")));
        test.Check("layout: pressing an item reaches the handler it was given", () => _itemPressed == 1);

        test.Click(At(_layout.Get<Button>("go")));
        test.Check("layout: so does a part of the layout itself", () => _goPresses == 1);

        test.Run(() => FillList(1));
        test.Hover(away, 0.3f);
        test.Check("layout: filling a container again replaces what was in it", () =>
            _layout.Get<StackPanel>("list").Children.Count == 1 && _items[0].Get<Label>("caption").Text == "item 1 of 1");

        test.Check("layout: the same file loads twice without the two getting in each other's way", () =>
        {
            var again = _loaded.LoadLayout(Path.Combine(_layoutDirectory, "menu.hor"));
            bool separate = again.Get<Button>("go") != _layout.Get<Button>("go");

            foreach (var root in again.Roots)
                _loaded.RemoveComponent(root);
            return separate;
        });

        test.Check("layout: one that fails says why and leaves nothing behind", () =>
        {
            int before = _loaded.Components.Count;
            try
            {
                _loaded.LoadLayout(Path.Combine(_layoutDirectory, "broken.hor"));
                return false;
            }
            catch (Exception e)
            {
                return e.Message.Contains("no_such_property") && e.Message.Contains("broken.hor") && _loaded.Components.Count == before;
            }
        });

        test.Check("layout: asking for a part that isn't there says which", () =>
        {
            try
            {
                _layout.Get<Button>("nowhere");
                return false;
            }
            catch (Exception e)
            {
                return e.Message.Contains("'nowhere'") && e.Message.Contains("Button");
            }
        });

        test.Check("layout: and so does asking for one as the wrong thing", () =>
        {
            try
            {
                _layout.Get<Button>("heading");
                return false;
            }
            catch (Exception e)
            {
                return e.Message.Contains("Label");
            }
        });

        test.Run(() => FillList(3));
        test.Hover(away);
    }
}
