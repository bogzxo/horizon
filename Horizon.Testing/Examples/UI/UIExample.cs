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
/// Showing off the UI library using a HUD pinned to the corners of the screen, a panel built by a HIDL script
/// and a panel built in C#. SPACE scales the scripted panel.
/// With the aid of Gemini's <c>selfTest</c>, a scripted pointer works its way through the controls instead of the mouse and
/// the results are printed, so the UI can be tested without touching anything.
/// </summary>
public class UIExample : Scene, ITestControls
{
    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } = [new("Space", "scale the scripted panel")];

    private readonly UICompositor _compositor;
    private readonly UISelfTest? _selfTest;

    private UIModule _hud, _scripted, _native;
    private ProgressBar _health, _stamina;
    private Label _status;
    private int _lockedPresses;

    public UIExample(bool selfTest = false)
    {
        // 1. Setup Camera and Compositor
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;
        _compositor = AddComponent(new UICompositor(cam));

        if (selfTest)
        {
            _selfTest = new UISelfTest();
            _compositor.PointerSource = () => _selfTest.Pointer;
        }
    }

    public override void PostInit()
    {
        base.PostInit();

        BuildHud();
        BuildScriptedPanel();
        BuildNativePanel();

        if (_selfTest is not null)
            ScriptSelfTest(_selfTest);

        Console.WriteLine("UIExample Initialized.");
        Engine.Graphics.ClearColor = new Vector4(0.22f, 0.27f, 0.36f, 1.0f);
    }

    // 2. Components anchored to the screen rather than placed by hand.
    private void BuildHud()
    {
        _hud = _compositor.CreateModule();

        _health = _hud.AddComponent(new ProgressBar
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -48),
            Progress = 0.75f
        });
        _stamina = _hud.AddComponent(new ProgressBar
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-24, -48),
            Progress = 0.4f
        });

        _status = _hud.AddComponent(new Label
        {
            Anchor = Origin.Bottom,
            Position = new Vector2(0, 24),
            TextScale = 0.3f
        });
    }

    // 3. Test HIDL Integration
    private void BuildScriptedPanel()
    {
        _scripted = _compositor.CreateModule();
        _scripted.Position = new Vector2(-300, 0);

        var (success, result) = _scripted.Runtime.Evaluate(@"
            let panel = compositor.stack({
                color: vec(0.1, 0.12, 0.17, 0.92),
                padding: 24,
                spacing: 16
            });

            compositor.label({ parent: panel, text: ""Built by a script"", text_scale: 0.5 });

            let pb = compositor.progress_bar({ parent: panel, progress: 0.5 });

            let row = compositor.stack({ parent: panel, direction: ""horizontal"" });

            let btnSub = compositor.button({
                parent: row,
                label: ""Sub 10%"",
                on_pressed: func() {
                    pb.progress = pb.progress - 0.1;
                    sld.value = pb.progress;
                }
            });

            let btnAdd = compositor.button({ parent: row, label: ""Add 10%"" });
            btnAdd.on_pressed = func() {
                pb.progress = pb.progress + 0.1;
                sld.value = pb.progress;
            };

            let sld = compositor.slider({
                parent: panel,
                value: 0.5,
                on_changed: func(value) {
                    pb.progress = value;
                }
            });

            let tgl = compositor.toggle({
                parent: panel,
                anchor: ""left"",
                label: ""Show percentage"",
                state: true,
                on_pressed: func(on) {
                    pb.show_text = on;
                }
            });
        ");

        if (!success)
        {
            Console.WriteLine($"HIDL Error: {result}");
        }
    }

    // 4. Test C# Component Creation
    private void BuildNativePanel()
    {
        _native = _compositor.CreateModule();
        _native.Position = new Vector2(300, 0);

        var panel = _native.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(24),
            Spacing = 16,
            Stretch = true
        });

        panel.Add(new Label("Built in C#") { TextScale = 0.5f });

        panel.Add(new Button("Hurt")
        {
            OnPressed = () => _health.Progress -= 0.25f
        });
        panel.Add(new Button("Heal")
        {
            OnPressed = () => _health.Progress += 0.25f
        });
        panel.Add(new Button("Locked")
        {
            Enabled = false,
            OnPressed = () => _lockedPresses++
        });

        var showHud = panel.Add(new ToggleButton("Show the HUD bars", startingState: true));
        showHud.OnPressed = () => _health.Visible = _stamina.Visible = showHud.State;
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _selfTest?.Update(dt);

        _status.Text = _compositor.IsPointerOverUI
            ? "The pointer is over the UI"
            : "The pointer is over the game";

        if (Engine.Input.Keyboard.WasPressed(Key.Space))
        {
            _scripted.Scale = _scripted.Scale.X >= 1.5f ? Vector2.One : _scripted.Scale + new Vector2(0.25f);
        }
    }

    private void ScriptSelfTest(UISelfTest test)
    {
        // The script's variables are the components it made.
        T Scripted<T>(string name) where T : UIComponent =>
            (T)UIComponent.FromScript(_scripted.Runtime.UserScope.Lookup(name))!;

        var bar = Scripted<ProgressBar>("pb");
        var sub = Scripted<Button>("btnSub");
        var add = Scripted<Button>("btnAdd");
        var slider = Scripted<Slider>("sld");
        var toggle = Scripted<ToggleButton>("tgl");

        var hurt = (Button)_native.Components[0].Children[1];
        var locked = (Button)_native.Components[0].Children[3];
        var showHud = (ToggleButton)_native.Components[0].Children[4];

        Func<Vector2> At(UIComponent component, float x = 0.5f) => () =>
        {
            UIRect bounds = component.Bounds;
            return component.Module!.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Center.Y));
        };

        test.Click(At(add));
        test.Click(At(add));
        test.Click(At(sub));
        test.Check("script buttons: 0.5 + 0.1 + 0.1 - 0.1", () => MathF.Abs(bar.Progress - 0.6f) < 0.001f);
        test.Check("script handler moved the slider too", () => MathF.Abs(slider.Value - 0.6f) < 0.001f);

        test.Hover(At(add), 1.2f);
        test.Check("hovering marks the button", () => add.IsHovered && !add.IsPressed);

        // Pressing on a button and letting go somewhere else is not a click.
        test.Drag(At(add), () => new Vector2(0, -350), holdAtStart: 1.2f);
        test.Check("a press dragged off the button didn't click it", () => MathF.Abs(bar.Progress - 0.6f) < 0.001f);
        test.Check("the pointer is over the game", () => !_compositor.IsPointerOverUI);

        test.Drag(At(slider, 0.1f), At(slider, 0.8f));
        test.Check("dragging the slider set it", () => slider.Value > 0.75f && slider.Value < 0.85f);
        test.Check("the slider's script handler set the bar", () => MathF.Abs(bar.Progress - slider.Value) < 0.001f);

        test.Click(At(toggle, 0.1f));
        test.Check("the toggle flipped and its handler hid the percentage", () => !toggle.State && !bar.ShowText);

        test.Click(At(hurt));
        test.Check("C# handler: 0.75 - 0.25", () => MathF.Abs(_health.Progress - 0.5f) < 0.001f);

        test.Click(At(locked));
        test.Check("a disabled button ignores presses", () => _lockedPresses == 0);
        test.Check("the pointer is over the UI", () => _compositor.IsPointerOverUI);

        test.Click(At(showHud, 0.1f));
        test.Check("the C# toggle hid the HUD bars", () => !showHud.State && !_health.Visible);

        // A scaled module has to be hit where it is drawn.
        float before = 0.0f;
        test.Run(() =>
        {
            before = bar.Progress;
            _scripted.Scale = new Vector2(1.25f);
        });
        test.Click(At(add));
        test.Check("a scaled module is hit where it is drawn", () => MathF.Abs(bar.Progress - MathF.Min(1.0f, before + 0.1f)) < 0.001f);

        test.Run(() => _scripted.Scale = Vector2.One);
        test.Hover(() => new Vector2(0, -350));
    }
}
