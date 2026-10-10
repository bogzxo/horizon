using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing;

/// <summary>
/// Swaps between the selector and the examples, and draws what sits over a running one. A strip in the bottom
/// left corner that says which example it is and gets you to the menu or the next one along, and a card on the
/// right with the keys it reacts to, each on a key cap, which F1 folds away.
/// <para>
/// It lives on the engine itself (<c>engine.AddEntity(...)</c> in Program) rather than in a scene, so it sticks
/// around whichever scene is up. That's the whole trick, an example is just a <see cref="Scene"/>, set with
/// <see cref="GameEngine.SetScene(Scene)"/> like any scene of your own, and has no idea it's being hosted. One you
/// can interact with says how through <see cref="ITestControls"/>.
/// </para>
/// </summary>
internal sealed class TestHost : GameObject
{
    private const float KEYS_TEXT_SCALE = 0.22f;
    private const float KEY_ROW_HEIGHT = 26.0f;

    // The host's own keys, listed under every example's
    private static readonly TestControl[] HostControls =
    [
        new("Esc", "back to the menu"),
        new("PgUp  PgDn", "previous and next example"),
        new("F1", "fold this away"),
        new("F3", "more of the performance numbers, or none"),
        new("F10", "the Skyline debugger, in a debug build"),
    ];

    private readonly IReadOnlyList<TestDefinition> tests;
    private readonly UIModule overlay;
    private readonly Label number, title, level, keysHint;

    // The card of keys, what to press in one column and what it does in the other
    private readonly StackPanel keys, inputs, actions;

    // What to switch to at the start of the next frame, a TestDefinition or the selector. Gets written from any
    // thread (a button press comes in on the simulation thread, the command line on the main one) and taken
    // with an Interlocked on the render thread, so nothing is ever switched to twice
    private object? request;

    // The running example's scene, and what was on the GPU before it started
    private Scene? scene;
    private Horizon.Graphics.ObjectManager.AssetSnapshot? assetsBeforeTest;

    // Running every check one after the other and leaving, see RunChecks
    private Queue<TestDefinition>? checksLeft;
    private Scene? judged;
    private int checksMade, checksFailed;

    public TestSelectorScene Selector { get; }

    /// <summary>The example on screen, null while the selector is.</summary>
    public TestDefinition? Running { get; private set; }

    public TestHost(IReadOnlyList<TestDefinition> tests)
    {
        Name = "Test Host";
        this.tests = tests;

        // Nothing here touches the GPU (UI components own nothing on it and the compositor makes its bits in its
        // own Initialize), so the whole overlay can be built right here in the constructor.
        // ForScreen gives the UI a camera of its own that it keeps the size of the window, which is exactly what a
        // HUD laid over everything wants. No camera to make, no remembering to resize it.
        var compositor = AddComponent(UICompositor.ForScreen());

        overlay = compositor.CreateModule();
        overlay.Enabled = false;

        /* The strip */

        var strip = overlay.AddComponent(new StackPanel
        {
            Anchor = Origin.BottomLeft,
            Position = new Vector2(16, 16),
            Direction = UIDirection.Horizontal,
            Color = HostStyle.Card,
            Radius = HostStyle.RADIUS,
            Padding = new UIEdges(8),
            Spacing = 8,
        });

        Button Small(string label, float width, Action pressed, string tooltip) => strip.Add(new Button(label)
        {
            Style = "button_flat",
            LabelScale = 0.26f,
            Size = new Vector2(width, 38),
            Tooltip = tooltip,
            OnPressed = pressed
        });

        Small("<", 38, () => Step(-1), "The example before this one (Page Up)");
        Small("Menu", 86, ShowSelector, "Back to the list (Esc)");
        Small(">", 38, () => Step(1), "The example after this one (Page Down)");

        strip.Add(new Spacer { Space = 2 });
        number = strip.Add(new Label { TextScale = 0.3f, Color = HostStyle.Accent });
        title = strip.Add(new Label { TextScale = 0.3f });
        level = strip.Add(new Label { TextScale = 0.2f, Color = HostStyle.Dim });
        strip.Add(new Spacer { Space = 2 });
        keysHint = strip.Add(new Label { TextScale = 0.2f, Color = HostStyle.Dim });
        strip.Add(new Spacer { Space = 2 });

        /* The keys */

        // Halfway up the right edge, which is where the examples have the least going on
        keys = overlay.AddComponent(new StackPanel
        {
            Anchor = Origin.Right,
            Position = new Vector2(-16, 0),
            Color = HostStyle.Card,
            Radius = HostStyle.RADIUS,
            Padding = new UIEdges(16, 14),
            Spacing = 10
        });
        keys.Add(new Label("Controls") { Anchor = Origin.Left, TextScale = 0.26f, Color = HostStyle.Accent });

        // Two columns with rows of the same height, so every key lines up with what it does
        var columns = keys.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 12 });
        inputs = columns.Add(new StackPanel { Spacing = 6 });
        actions = columns.Add(new StackPanel { Spacing = 6 });

        ShowKeys(true);

        Selector = new TestSelectorScene(this, tests);
    }

    /// <summary>
    /// Asks for an example to be started. Happens at the start of the next frame, so it's safe from any thread
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

    /// <summary>
    /// Runs every check there is, one after the other, prints how each went and closes the window, with the number
    /// that failed as what the process leaves with. For a build machine, or for you after mucking about in UIX.
    /// </summary>
    public void RunChecks()
    {
        checksLeft = new Queue<TestDefinition>(tests.Where(test => test.Level == TestLevel.Checks));
        if (checksLeft.TryDequeue(out var first))
            Start(first);
    }

    public override void Initialize()
    {
        base.Initialize();

        // The overlay's GPU stuff has to exist before any example starts. Everything made after the snapshot in
        // Switch counts as that example's and gets freed with it, and a UI with its renderer freed out from under
        // it is not a good time.
        InitializeAll();

        // Scenes are swapped in an Exclusive, on the render thread at the start of a frame, with the simulation
        // parked. The render thread because leaving an example frees what it had on the GPU, and only that thread
        // may touch the GPU. The simulation parked because the example we're leaving is updated right up until
        // then, and pulling stuff out from under a running tick is how you get a crash that only happens on Tuesdays.
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

        if (checksLeft is not null)
        {
            JudgeCheck();
            return;
        }

        // Simulation thread, which is where input is read. WasPressed is true for the one update after the key
        // went down, however long it's held, so this fires once per press.
        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.Escape))
            ShowSelector();

        if (keyboard.WasPressed(Key.PageUp)) Step(-1);
        if (keyboard.WasPressed(Key.PageDown)) Step(1);

        // Stays as you left it from one example to the next
        if (keyboard.WasPressed(Key.F1))
            ShowKeys(!keys.Visible);

        // For checking the swapchain follows, the loop numbers (HORIZON_LOG_LOOPS) say whether it did
        if (keyboard.WasPressed(Key.F11))
        {
            var display = Engine.WindowManager.Display;
            Engine.WindowManager.Apply(display with { VSync = !display.VSync });
            Horizon.Logging.Log.Info($"[TestHost] VSync {(display.VSync ? "off" : "on")}.");
        }
    }

    /// <summary>Helper method to go to the example before or after the running one, round the ends of the list.</summary>
    private void Step(int by)
    {
        if (Running is null) return;

        int index = 0;
        for (int i = 0; i < tests.Count; i++)
        {
            if (ReferenceEquals(tests[i], Running)) index = i;
        }

        Start(tests[((index + by) % tests.Count + tests.Count) % tests.Count]);
    }

    /// <summary>Helper method to see whether the running check has finished, and move on to the next or leave if it has.</summary>
    private void JudgeCheck()
    {
        if (scene is not ISelfCheck check || !check.Finished || ReferenceEquals(scene, judged))
            return;

        judged = scene;
        checksMade += check.Count;
        checksFailed += check.Failed;
        Console.WriteLine($"[Checks] {Running!.Id}, {check.Count - check.Failed} of {check.Count} held.");

        if (checksLeft!.TryDequeue(out var next))
        {
            Start(next);
            return;
        }

        Console.WriteLine($"[Checks] {checksMade - checksFailed} of {checksMade} checks passed.");
        Environment.ExitCode = checksFailed;
        Engine.Exit();
    }

    /// <summary>
    /// Helper method to leave whatever is running and switch to an example or the selector. Render thread, with the
    /// simulation parked (see <see cref="Initialize"/>).
    /// </summary>
    private void Switch(object next)
    {
        if (Running is not null && scene is not null && assetsBeforeTest is not null)
        {
            // The scene manager frees what a scene noted as its own (Scene.Assets) once it's left, so this is belt
            // and braces, everything that went onto the GPU since the example started goes, noted or not. They are
            // made fresh every time, so whatever slipped through would leave another copy behind on every visit,
            // and you'd only find out when the VRAM is full of shit.
            scene.Dispose();
            int freed = Engine.ObjectManager.ReleaseSince(assetsBeforeTest);
            Console.WriteLine($"Left '{Running.Name}', freed {freed} GPU objects it had created.");
        }

        if (next is TestDefinition test)
        {
            Running = test;
            number.Text = test.Number > 0 ? $"{test.Number:00}" : "check";
            title.Text = test.Name;
            level.Text = TestCatalog.NameOf(test.Level);
            overlay.Enabled = true;

            Console.WriteLine($"Running '{test.Name}'.");
            assetsBeforeTest = Engine.ObjectManager.Snapshot();
            Engine.SetScene(scene = test.Create());

            // One that reacts to nothing still gets the host's keys listed
            ListControls(scene is ITestControls interactive ? interactive.Controls : []);
        }
        else
        {
            Running = null;
            scene = null;
            overlay.Enabled = false;

            // The selector is Persistent, so it comes back exactly as you left it
            Engine.SetScene(Selector);
        }
    }

    /// <summary>Helper method to show or hide the card of keys. The strip always says how to get it back.</summary>
    private void ShowKeys(bool show)
    {
        keys.Visible = show;
        keysHint.Text = show ? "F1 hides the keys" : "F1 shows the keys";
    }

    /// <summary>Helper method to swap the list of keys for an example's, followed by the host's own.</summary>
    private void ListControls(IReadOnlyList<TestControl> controls)
    {
        foreach (var row in inputs.Children.ToArray())
            inputs.Remove(row);
        foreach (var row in actions.Children.ToArray())
            actions.Remove(row);

        foreach (var control in controls)
            AddControl(control, dim: false);

        // Dimmer, they're the same in every example
        foreach (var control in HostControls)
            AddControl(control, dim: true);
    }

    /// <summary>Helper method to add one line to the card, the key on a cap and what it does next to it.</summary>
    private void AddControl(TestControl control, bool dim)
    {
        // A cap is a little rounded panel as wide as what is written on it, pushed up against the actions
        var cap = inputs.Add(new StackPanel
        {
            Anchor = Origin.Right,

            // Sideways, so the one thing in it sits in the middle of its height
            Direction = UIDirection.Horizontal,
            Color = dim ? HostStyle.Key with { W = 0.5f } : HostStyle.Key,
            Radius = 5.0f,
            Padding = new UIEdges(9, 0),
            Size = new Vector2(0, KEY_ROW_HEIGHT)
        });
        cap.Add(new Label(control.Input) { TextScale = KEYS_TEXT_SCALE, Color = dim ? HostStyle.Dim : HostStyle.Accent });

        actions.Add(new Label(control.Action)
        {
            Anchor = Origin.Left,
            Align = Origin.Left,
            Size = new Vector2(0, KEY_ROW_HEIGHT),
            TextScale = KEYS_TEXT_SCALE,
            Color = dim ? HostStyle.Dim : null
        });
    }
}
