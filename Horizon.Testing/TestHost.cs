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
/// test's name and a way back, and listens for ESC.
/// A test is therefore just a scene, and doesn't have to know it is being hosted.
/// </summary>
internal sealed class TestHost : GameObject
{
    private readonly UIModule bar;
    private readonly Label title;

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
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(10),
            Spacing = 14
        });

        row.Add(new Button("Back")
        {
            LabelScale = 0.3f,
            Size = new Vector2(96, 40),
            OnPressed = ShowSelector
        });
        title = row.Add(new Label { TextScale = 0.3f });

        Selector = new TestSelectorScene(this, tests);
    }

    /// <summary>Asks for a test to be started. It happens at the next frame; safe from any thread.</summary>
    public void Start(TestDefinition test) => request = test;

    /// <summary>Asks for the selector to be shown. It happens at the next frame; safe from any thread.</summary>
    public void ShowSelector() => request = Selector;

    public override void Initialize()
    {
        base.Initialize();

        // The bar's own GPU resources have to exist before any test starts, or they would be counted
        // as that test's and freed along with it.
        InitializeAll();
    }

    public override void Render(float dt, object? obj = null)
    {
        // Scenes are switched here, on the GL thread, because leaving a test frees what it put on the GPU.
        if (Interlocked.Exchange(ref request, null) is { } next)
            Switch(next);

        base.Render(dt, obj);
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

            Console.WriteLine($"Running '{test.Name}'. ESC or Back returns to the selector.");
            assetsBeforeTest = Engine.ObjectManager.Snapshot();
            Engine.SetScene(scene = test.Create());
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

        if (Running is not null && Engine.InputManager.KeyboardManager.IsKeyPressed(Key.Escape))
            ShowSelector();
    }
}
