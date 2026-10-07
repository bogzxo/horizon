using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

using Button = Horizon.Rendering.UIX.Components.Button;

namespace Horizon.Testing;

/// <summary>
/// Switches between the selector and the tests. It lives on the engine rather than in a scene, so it
/// stays around whichever scene is showing. While a test runs it draws a small bar over it with the
/// test's name and a way back, lists the keys the test reacts to, and listens for ESC.
/// A test is therefore just a scene, and doesn't have to know it is being hosted. One that can be
/// interacted with says how through <see cref="ITestControls"/>.
/// </summary>
internal sealed class TestHost : GameObject
{
    private const float KeysTextScale = 0.25f;

    private static readonly Vector4 PanelColor = new(0.1f, 0.12f, 0.17f, 0.92f);
    private static readonly Vector4 InputColor = new(1.0f, 0.8f, 0.45f, 1.0f);
    private static readonly Vector4 DimColor = new(0.93f, 0.95f, 1.0f, 0.6f);

    // The keys that are the host's own, listed under those of every test.
    private static readonly TestControl[] HostControls = [new("Esc", "back to the tests")];

    private readonly UIModule bar;
    private readonly Label title, keysHint;

    // The list of keys: what to press on the left, what it does on the right.
    private readonly StackPanel keys, inputs, actions;

    // What to switch to at the next frame: a TestDefinition, or the selector scene.
    private object? request;

    // The running test's scene, and what was on the GPU before it started.
    private Scene? scene;
    private Horizon.OpenGL.Managers.ObjectManager.AssetSnapshot? assetsBeforeTest;

    public TestSelectorScene Selector { get; }

    /// <summary>The test on screen, null while the selector is.</summary>
    public TestDefinition? Running { get; private set; }

    public TestHost(IReadOnlyList<TestDefinition> tests)
    {
        Name = "Test Host";

        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        var compositor = AddComponent(new UICompositor(camera));

        bar = compositor.CreateModule();
        bar.Enabled = false;

        var row = bar.AddComponent(new StackPanel
        {
            Anchor = Origin.BottomLeft,
            Position = new Vector2(16, 16),
            Direction = UIDirection.Horizontal,
            Color = PanelColor,
            Padding = new UIEdges(10),
            Spacing = 14,
        });

        row.Add(new Button("Back")
        {
            LabelScale = 0.3f,
            Size = new Vector2(96, 40),
            OnPressed = ShowSelector
        });
        title = row.Add(new Label { TextScale = 0.3f });
        keysHint = row.Add(new Label { TextScale = KeysTextScale, Color = DimColor });

        // Halfway up the right edge, which is where the tests have the least going on.
        keys = bar.AddComponent(new StackPanel
        {
            Anchor = Origin.Right,
            Position = new Vector2(-16, 0),
            Direction = UIDirection.Horizontal,
            Color = PanelColor,
            Padding = new UIEdges(14),
            Spacing = 16
        });

        // Two columns of labels at the same scale, so every key lines up with what it does.
        inputs = keys.Add(new StackPanel { Spacing = 6 });
        actions = keys.Add(new StackPanel { Spacing = 6 });

        ShowKeys(true);

        Selector = new TestSelectorScene(this, tests);
    }

    /// <summary>Shows or hides the list of keys. The bar always says how to get it back.</summary>
    private void ShowKeys(bool show)
    {
        keys.Visible = show;
        keysHint.Text = show ? "F1: hide keys" : "F1: show keys";
    }

    /// <summary>Replaces the list of keys with those of a test, followed by the host's own.</summary>
    private void ListControls(IReadOnlyList<TestControl> controls)
    {
        foreach (var label in inputs.Children.ToArray())
            inputs.Remove(label);
        foreach (var label in actions.Children.ToArray())
            actions.Remove(label);

        foreach (var control in controls)
            AddControl(control, InputColor, null);

        // Dimmer, as they are the same in every test.
        foreach (var control in HostControls)
            AddControl(control, DimColor, DimColor);
    }

    private void AddControl(TestControl control, Vector4 inputColor, Vector4? actionColor)
    {
        inputs.Add(new Label(control.Input) { Anchor = Origin.Left, TextScale = KeysTextScale, Color = inputColor });
        actions.Add(new Label(control.Action) { Anchor = Origin.Left, TextScale = KeysTextScale, Color = actionColor });
    }

    /// <summary>Asks for a test to be started. It happens at the next frame; safe from any thread.</summary>
    public void Start(TestDefinition test)
    {
        request = test;
        Engine.WindowManager.RequestExclusive();
    }

    /// <summary>Asks for the selector to be shown. It happens at the next frame; safe from any thread.</summary>
    public void ShowSelector()
    {
        request = Selector;
        Engine.WindowManager.RequestExclusive();
    }

    public override void Initialize()
    {
        base.Initialize();

        // The bar's own GPU resources have to exist before any test starts, or they would be counted
        // as that test's and freed along with it.
        InitializeAll();

        // Scenes are switched on the GL thread, because leaving a test frees what it put on the GPU, and with the
        // simulation standing still, because the test that is left is still being updated until then
        Engine.WindowManager.Exclusive += _ =>
        {
            if (Interlocked.Exchange(ref request, null) is { } next)
                Switch(next);
        };
    }

    private void Switch(object next)
    {
        if (Running is not null && scene is not null && assetsBeforeTest is not null)
        {
            // The engine keeps a scene's GPU resources until it shuts down. A test is made anew every
            // time it is started, so without this each visit would leave another set behind.
            scene.Dispose();
            int freed = Engine.ObjectManager.ReleaseSince(assetsBeforeTest);
            Console.WriteLine($"Left '{Running.Name}', freed {freed} GPU objects it had created.");
        }

        if (next is TestDefinition test)
        {
            Running = test;
            title.Text = test.Name;
            bar.Enabled = true;

            Console.WriteLine($"Running '{test.Name}'.");
            assetsBeforeTest = Engine.ObjectManager.Snapshot();
            Engine.SetScene(scene = test.Create());

            // A test that reacts to nothing still gets the host's keys listed.
            ListControls(scene is ITestControls interactive ? interactive.Controls : []);
        }
        else
        {
            Running = null;
            scene = null;
            bar.Enabled = false;

            // The selector is kept and shown again as it was left.
            Engine.SetScene(Selector);
        }
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        if (Running is null)
            return;

        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.Escape))
            ShowSelector();

        // Stays as it was left from one test to the next.
        if (keyboard.WasPressed(Key.F1))
            ShowKeys(!keys.Visible);
    }
}
