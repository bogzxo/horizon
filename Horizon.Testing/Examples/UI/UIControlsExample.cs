using System;
using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.UI;

/// <summary>
/// The controls of the UI library that came after the first ones: the dropdown and the list it opens on top of
/// everything else, the slider with ends and steps of its own, the colour picker, and the entrances a layout
/// can give its components (one after the other, if their container says so).
/// With a self-test a scripted pointer works its way through all of it and the results are printed.
/// </summary>
public class UIControlsExample : Scene, ITestControls
{
    // The screen the test is laid out for. The window of the tests is that size, so the UI starts at a scale of 1.
    private static readonly Vector2 DesignSize = new(1600, 900);

    private static readonly string[] Fruits =
        ["apple", "banana", "cherry", "damson", "elderberry", "fig", "grape", "honeydew", "kiwi", "lemon", "mango", "nectarine"];

    // How many seconds later each button of the card makes its entrance than the one before it
    private const float STAGGER = 0.3f;

    // A layout with entrances: the card pops up, its buttons slide in from the left one after the other.
    private const string CARD_LAYOUT = """
        let card = compositor.stack({
            color: vec(0.1, 0.12, 0.17, 0.92),
            padding: 20,
            spacing: 10,
            stagger: 0.3,
            intro: "pop",
            intro_time: 0.3
        });
        let heading = compositor.label({ parent: card, text: "Entrances out of a layout", text_scale: 0.3 });
        let first = compositor.button({ parent: card, label: "one", size: vec(260, 0), intro: "slide", intro_time: 0.3, intro_offset: vec(-200, 0) });
        let second = compositor.button({ parent: card, label: "two", size: vec(260, 0), intro: "slide", intro_time: 0.3, intro_offset: vec(-200, 0) });
        let third = compositor.button({ parent: card, label: "three", size: vec(260, 0), intro: "fade", intro_time: 0.3 });
        """;

    // The same controls as the ones built in code, written the way a layout file writes them.
    private const string SCRIPTED_LAYOUT = """
        let pick = compositor.dropdown({ options: ["a", "b", "c"], value: "b", max_rows: 2, visible: false });
        let level = compositor.slider({ min: 10, max: 20, step: 2, value: 15, visible: false });
        let paint = compositor.color_picker({ color: vec(0.2, 0.4, 0.6, 0.8), show_alpha: false, visible: false });
        """;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Space", "play the entrances again"),
        new("Mouse", "press, drag, scroll")
    ];

    private readonly UICompositor _compositor;
    private readonly UISelfTest? _selfTest;

    private UIModule _controls = null!, _cards = null!;
    private Dropdown _dropdown = null!;
    private Slider _slider = null!;
    private ColorPicker _picker = null!;
    private Panel _swatch = null!;
    private Label _sliderValue = null!;
    private UILayout _card = null!;

    private int _dropdownChanges, _sliderChanges, _pickerChanges;

    public UIControlsExample(bool selfTest = false)
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

        _cards = _compositor.CreateModule();
        _cards.Position = new Vector2(380, 0);
        _card = UILayout.LoadCode(_cards, CARD_LAYOUT);

        if (_selfTest is not null)
            ScriptSelfTest(_selfTest);

        Engine.GL.ClearColor(0.22f, 0.27f, 0.36f, 1.0f);
    }

    private void BuildControls()
    {
        _controls = _compositor.CreateModule();
        _controls.Position = new Vector2(-380, 60);

        var panel = _controls.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(20),
            Spacing = 12
        });

        panel.Add(new Label("Dropdown, slider, colours") { TextScale = 0.3f });

        // More options than the list shows at once, and a list that opens over what is underneath it.
        _dropdown = panel.Add(new Dropdown(Fruits)
        {
            Size = new Vector2(300, 38),
            TextScale = 0.24f,
            MaxRows = 5,
            Index = 4,
            OnChanged = _ => _dropdownChanges++
        });

        _slider = panel.Add(new Slider
        {
            Size = new Vector2(300, 28),
            Min = 0,
            Max = 100,
            Step = 5,
            Value = 20,
            OnChanged = _ => _sliderChanges++
        });
        _sliderValue = panel.Add(new Label { TextScale = 0.22f });

        _swatch = panel.Add(new Panel { Size = new Vector2(300, 26), Color = Vector4.One });
        _picker = panel.Add(new ColorPicker
        {
            Size = new Vector2(300, 150),
            Color = Vector4.One,
            OnChanged = colour =>
            {
                _pickerChanges++;
                _swatch.Color = colour;
            }
        });
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _selfTest?.Update(dt);

        _sliderValue.Text = $"{_slider.Value:0} of {_slider.Min:0} to {_slider.Max:0}, in steps of {_slider.Step:0}";

        if (Engine.Input.Keyboard.WasPressed(Key.Space))
            _card.PlayIntros();
    }

    private void ScriptSelfTest(UISelfTest test)
    {
        Func<Vector2> At(UIComponent component, float x = 0.5f, float y = 0.5f) => () =>
        {
            UIRect bounds = component.Bounds;
            return component.Module!.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Min.Y + bounds.Height * y));
        };

        Func<Vector2> In(Func<UIRect> area, float x, float y) => () => _controls.ToWorld(area().Min + area().Size * new Vector2(x, y));
        Func<Vector2> Row(int row) => () => _controls.ToWorld(_dropdown.RowBounds(row).Center);

        Func<Vector2> away = () => new Vector2(0, -420);
        bool Near(float a, float b, float within = 0.5f) => MathF.Abs(a - b) <= within;

        var first = _card.Get<Button>("first");
        var second = _card.Get<Button>("second");
        var third = _card.Get<Button>("third");
        var card = _card.Get<StackPanel>("card");

        /* Entrances, which started the moment the layout was loaded */

        test.Run(() => _card.PlayIntros());
        test.Check("intro: everything with an entrance starts out hidden", () =>
            third.Opacity == 0.0f && third.Tweens.Count > 0 && card.Tweens.Count > 0 && first.VisualOffset.X < -100);

        test.Hover(away, 0.1f);
        test.Check("intro: the first one is on its way while the last one waits its turn", () =>
            first.Opacity > 0.5f && third.Opacity == 0.0f);

        test.Hover(away, 1.2f);
        test.Check("intro: in the end everything is where it belongs", () =>
            new UIComponent[] { card, first, second, third }.All(component =>
                component.Opacity == 1.0f && component.VisualOffset == Vector2.Zero && component.VisualScale == Vector2.One && component.Tweens.Count == 0));
        test.Check("intro: a container hands its stagger on to what is in it", () =>
            card.Stagger == STAGGER && first.Intro == UIIntro.Slide && third.Intro == UIIntro.Fade && card.Intro == UIIntro.Pop);

        /* The dropdown */

        float sliderBefore = 0;
        int expected = 0;

        test.Check("dropdown: shut, it shows what is chosen", () => !_dropdown.IsOpen && _dropdown.Value == "elderberry" && _controls.Popup is null);

        test.Click(At(_dropdown));
        test.Check("dropdown: a click opens its list under it, on top of the module", () =>
            _dropdown.IsOpen && _controls.Popup == _dropdown && Near(_dropdown.ListBounds.Max.Y, _dropdown.Bounds.Min.Y)
            && Near(_dropdown.ListBounds.Height, 5 * _dropdown.Bounds.Height + 4));
        test.Check("dropdown: with what is chosen in sight", () => _dropdown.FirstVisible == 2);

        // The slider is under the open list. A click there is a click on the list.
        test.Run(() =>
        {
            sliderBefore = _slider.Value;
            Vector2 point = _slider.Bounds.Center;
            for (int row = 0; row < 5; row++)
            {
                if (_dropdown.RowBounds(row).Contains(point))
                    expected = _dropdown.FirstVisible + row;
            }
        });
        test.Click(At(_slider));
        test.Check("dropdown: the list gets the pointer before what is under it", () =>
            _slider.Value == sliderBefore && _sliderChanges == 0 && _dropdown.Index == expected && _dropdownChanges == 1);
        test.Check("dropdown: choosing closes the list", () => !_dropdown.IsOpen && _controls.Popup is null);

        test.Click(At(_dropdown));
        test.Hover(Row(2), 0.1f);
        test.Run(() =>
        {
            expected = Math.Min(_dropdown.FirstVisible + 3, Fruits.Length - 5);
            _compositor.Scroll(-3);
        });
        test.Hover(Row(2), 0.2f);
        test.Check("dropdown: the wheel moves through a list that is longer than it shows", () => _dropdown.FirstVisible == expected && _dropdown.IsOpen);

        test.Click(Row(0));
        test.Check("dropdown: a row is the option it shows after scrolling", () => _dropdown.Index == expected && _dropdownChanges == 2);

        test.Click(At(_dropdown));
        test.Click(away);
        test.Check("dropdown: pressing anywhere else closes the list and changes nothing", () =>
            !_dropdown.IsOpen && _dropdown.Index == expected && _dropdownChanges == 2);

        test.Click(At(_dropdown));
        test.Click(At(_dropdown));
        test.Check("dropdown: and so does pressing the dropdown itself", () => !_dropdown.IsOpen && _dropdownChanges == 2);

        /* The slider */

        test.Click(At(_slider));
        test.Check("slider: halfway along is halfway between its ends", () => _slider.Value == 50.0f && Near(_slider.Progress, 0.5f, 0.001f) && _sliderChanges > 0);

        test.Click(At(_slider, 0.62f));
        test.Check("slider: the value keeps to its steps", () => _slider.Value % 5.0f == 0.0f && _slider.Value is >= 55.0f and <= 70.0f);

        test.Drag(At(_slider), At(_slider, 1.2f));
        test.Check("slider: dragging past the end stops at the end", () => _slider.Value == 100.0f);
        test.Drag(At(_slider, 0.9f), At(_slider, -0.2f));
        test.Check("slider: and at the start", () => _slider.Value == 0.0f);

        /* The colour picker */

        test.Click(In(() => _picker.ShadeBounds, 0.96f, 0.96f));
        test.Check("colour: the corner of the square is the hue at its fullest", () =>
            _picker.Color is { X: > 0.85f, Y: < 0.15f, Z: < 0.15f, W: 1.0f } && _pickerChanges >= 1);

        test.Click(In(() => _picker.HueBounds, 0.5f, 0.34f));
        test.Check("colour: the strip next to it picks the hue", () => _picker.Color is { X: < 0.15f, Y: > 0.85f, Z: < 0.3f });

        test.Drag(In(() => _picker.ShadeBounds, 0.9f, 0.9f), In(() => _picker.ShadeBounds, 0.02f, 0.98f));
        test.Check("colour: towards the other side of the square it washes out", () => _picker.Color is { X: > 0.85f, Y: > 0.85f, Z: > 0.85f });
        test.Click(In(() => _picker.ShadeBounds, 0.9f, 0.9f));
        test.Check("colour: without losing the hue on the way through white", () => _picker.Color is { X: < 0.25f, Y: > 0.8f, Z: < 0.35f });

        test.Click(In(() => _picker.AlphaBounds, 0.5f, 0.5f));
        test.Check("colour: the last strip is how solid it is", () => Near(_picker.Color.W, 0.5f, 0.06f));
        test.Check("colour: whoever listens hears about every change", () => _swatch.Color == _picker.Color && _pickerChanges >= 5);

        test.Run(() => _picker.Color = new Vector4(0.2f, 0.4f, 0.6f, 0.8f));
        test.Check("colour: a colour that is set comes back the same", () =>
            Vector4.Distance(_picker.Color, new Vector4(0.2f, 0.4f, 0.6f, 0.8f)) < 0.001f && _pickerChanges < 100);
        test.Check("colour: there and back between the two ways of writing one", () =>
        {
            var (hue, saturation, value) = ColorPicker.ToHsv(new Vector3(0.9f, 0.3f, 0.1f));
            return Vector3.Distance(ColorPicker.ToRgb(hue, saturation, value), new Vector3(0.9f, 0.3f, 0.1f)) < 0.001f;
        });

        /* The same controls out of a layout */

        test.Check("layout: a script makes them with the same properties", () =>
        {
            var scripted = UILayout.LoadCode(_cards, SCRIPTED_LAYOUT);

            var pick = scripted.Get<Dropdown>("pick");
            var level = scripted.Get<Slider>("level");
            var paint = scripted.Get<ColorPicker>("paint");

            // 15 isn't a step from 10 in twos, the nearest one is
            return pick is { Value: "b", MaxRows: 2, Options.Length: 3 }
                && level is { Min: 10.0f, Max: 20.0f, Value: 16.0f }
                && !paint.ShowAlpha && Vector4.Distance(paint.Color, new Vector4(0.2f, 0.4f, 0.6f, 0.8f)) < 0.001f;
        });
        test.Check("layout: and the library lists them among what a script can make", () =>
            UIModule.Kinds.Contains("dropdown") && UIModule.Kinds.Contains("color_picker") && UIModule.Kinds.Contains("slider"));

        test.Hover(away, 0.2f);
    }
}
