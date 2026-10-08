using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing;

/// <summary>
/// Swaps between the selector and the tests, and draws the little bar over a running test: its name, a way back
/// and the keys it reacts to. ESC gets you back as well.
/// <para>
/// It lives on the engine itself (<c>engine.AddEntity(...)</c> in Program) rather than in a scene, so it sticks
/// around whichever scene is up. That's the whole trick: a test is just a <see cref="Scene"/>, set with
/// <see cref="GameEngine.SetScene(Scene)"/> like any scene of your own, and has no idea it's being hosted. One you
/// can interact with says how through <see cref="ITestControls"/>.
/// </para>
/// </summary>
internal sealed class TestHost : GameObject
{
    private const float KeysTextScale = 0.25f;

    private static readonly Vector4 PanelColor = new(0.1f, 0.12f, 0.17f, 0.92f);
    private static readonly Vector4 InputColor = new(1.0f, 0.8f, 0.45f, 1.0f);
    private static readonly Vector4 DimColor = new(0.93f, 0.95f, 1.0f, 0.6f);

    // The host's own keys, listed under every test's
    private static readonly TestControl[] HostControls = [new("Esc", "back to the tests")];

    private readonly UIModule bar;
    private readonly Label title, keysHint;

    // The list of keys: what to press in one column, what it does in the other
    private readonly StackPanel keys, inputs, actions;

    // What to switch to at the start of the next frame, a TestDefinition or the selector. Gets written from any
    // thread (a button press comes in on the simulation thread, the command line on the main one) and taken
    // with an Interlocked on the render thread, so nothing is ever switched to twice
    private object? request;

    // The running test's scene, and what was on the GPU before it started
    private Scene? scene;
    private Horizon.OpenGL.Managers.ObjectManager.AssetSnapshot? assetsBeforeTest;

    public TestSelectorScene Selector { get; }

    /// <summary>The test on screen, null while the selector is.</summary>
    public TestDefinition? Running { get; private set; }

    public TestHost(IReadOnlyList<TestDefinition> tests)
    {
        Name = "Test Host";

        // Nothing here touches the GPU (UI components own nothing on it and the compositor makes its bits in its
        // own Initialize), so the whole bar can be built right here in the constructor.
        // ForScreen gives the UI a camera of its own that it keeps the size of the window, which is exactly what a
        // HUD laid over everything wants. No camera to make, no remembering to resize it.
        var compositor = AddComponent(UICompositor.ForScreen());

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

        // Halfway up the right edge, which is where the tests have the least going on
        keys = bar.AddComponent(new StackPanel
        {
            Anchor = Origin.Right,
            Position = new Vector2(-16, 0),
            Direction = UIDirection.Horizontal,
            Color = PanelColor,
            Padding = new UIEdges(14),
            Spacing = 16
        });

        // Two columns of labels at the same scale, so every key lines up with what it does
        inputs = keys.Add(new StackPanel { Spacing = 6 });
        actions = keys.Add(new StackPanel { Spacing = 6 });

        ShowKeys(true);

        Selector = new TestSelectorScene(this, tests);
    }

    /// <summary>
    /// Asks for a test to be started. Happens at the start of the next frame, so it's safe from any thread
    /// (and before <see cref="GameEngine.Run()"/>, which is how the command line does it).
    /// </summary>
    public void Start(TestDefinition test)
    {
        request = test;
        Engine.WindowManager.RequestExclusive();
    }

    /// <summary>Asks for the selector to be shown. Happens at the start of the next frame, safe from any thread.</summary>
    public void ShowSelector()
    {
        request = Selector;
        Engine.WindowManager.RequestExclusive();
    }

    public override void Initialize()
    {
        base.Initialize();

        // The bar's GPU stuff has to exist before any test starts. Everything made after the snapshot in Switch
        // counts as that test's and gets freed with it, and a UI with its renderer freed out from under it is not
        // a good time.
        InitializeAll();

        // Scenes are swapped in an Exclusive: on the render thread at the start of a frame, with the simulation
        // parked. The render thread because leaving a test frees what it had on the GPU, and only that thread may
        // touch GL. The simulation parked because the test we're leaving is updated right up until then, and
        // pulling stuff out from under a running tick is how you get a crash that only happens on Tuesdays.
        Engine.WindowManager.Exclusive += _ =>
        {
            if (Interlocked.Exchange(ref request, null) is { } next)
                Switch(next);
        };
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        if (Running is null)
            return;

        // Simulation thread, which is where input is read. WasPressed is true for the one update after the key
        // went down, however long it's held, so this fires once per press.
        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.Escape))
            ShowSelector();

        // Stays as you left it from one test to the next
        if (keyboard.WasPressed(Key.F1))
            ShowKeys(!keys.Visible);
    }

    /// <summary>
    /// Helper method to leave whatever is running and switch to a test or the selector. Render thread, with the
    /// simulation parked (see <see cref="Initialize"/>).
    /// </summary>
    private void Switch(object next)
    {
        if (Running is not null && scene is not null && assetsBeforeTest is not null)
        {
            // The scene manager frees what a scene noted as its own (Scene.Assets) once it's left, so this is belt
            // and braces: everything that went onto the GPU since the test started goes, noted or not. Tests are
            // made fresh every time, so whatever slipped through would leave another copy behind on every visit,
            // and you'd only find out when the VRAM is full of shit.
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

            // A test that reacts to nothing still gets the host's keys listed
            ListControls(scene is ITestControls interactive ? interactive.Controls : []);
        }
        else
        {
            Running = null;
            scene = null;
            bar.Enabled = false;

            // The selector is Persistent, so it comes back exactly as you left it
            Engine.SetScene(Selector);
        }
    }

    /// <summary>Helper method to show or hide the list of keys. The bar always says how to get it back.</summary>
    private void ShowKeys(bool show)
    {
        keys.Visible = show;
        keysHint.Text = show ? "F1: hide keys" : "F1: show keys";
    }

    /// <summary>Helper method to swap the list of keys for a test's, followed by the host's own.</summary>
    private void ListControls(IReadOnlyList<TestControl> controls)
    {
        foreach (var label in inputs.Children.ToArray())
            inputs.Remove(label);
        foreach (var label in actions.Children.ToArray())
            actions.Remove(label);

        foreach (var control in controls)
            AddControl(control, InputColor, null);

        // Dimmer, they're the same in every test
        foreach (var control in HostControls)
            AddControl(control, DimColor, DimColor);
    }

    /// <summary>Helper method to add one line to the list of keys. A null colour is the skin's text colour.</summary>
    private void AddControl(TestControl control, Vector4 inputColor, Vector4? actionColor)
    {
        inputs.Add(new Label(control.Input) { Anchor = Origin.Left, TextScale = KeysTextScale, Color = inputColor });
        actions.Add(new Label(control.Action) { Anchor = Origin.Left, TextScale = KeysTextScale, Color = actionColor });
    }
}
