using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing;

/// <summary>
/// The menu you land on. Every example down the left, simplest first under the heading of its
/// <see cref="TestLevel"/>, and on the right whatever there is to say about the one you are on, what it shows, the
/// parts of the engine it is about, where its code is and how to start straight in it. The mouse works, and so do
/// the arrow keys and a pad, every module has a <see cref="UINavigator"/> that walks its controls by where they are.
/// <para>
/// It's a UI and nothing else, so it's a decent little example of UIX built straight from C# too. A
/// <see cref="UICompositor.ForScreen"/> UI laid out for a <see cref="UICompositor.DesignSize"/>, stacks inside stacks,
/// a <see cref="ScrollPanel"/> for a list that's taller than the screen, and labels that wrap.
/// </para>
/// </summary>
internal sealed class TestSelectorScene : Scene
{
    // Laid out for 1600 by 900. A smaller window gets the whole lot shrunk to fit, a bigger one gets it bigger, and
    // either way nothing falls off the edge
    private static readonly Vector2 DesignSize = new(1600, 900);

    private const float LIST_WIDTH = 560.0f;
    private const float DETAILS_WIDTH = 720.0f;
    private const float BODY_HEIGHT = 640.0f;
    private const float ROW_HEIGHT = 42.0f;
    private const float ROW_SPACING = 6.0f;

    private readonly TestHost host;
    private readonly UIModule module;
    private readonly Dictionary<UIComponent, TestDefinition> rows = [];

    private readonly Label where, name, summary, shows, command, source;
    private readonly Button run;
    private TestDefinition? shown;

    // The host keeps the selector, so it comes back exactly as you left it, scrolled to wherever you were
    public override bool Persistent => true;

    public TestSelectorScene(TestHost host, IReadOnlyList<TestDefinition> tests)
    {
        this.host = host;

        // No ActiveCamera, there's nothing in the world to look at, and a scene that doesn't set one is seen through
        // the engine's. The UI brings its own camera with ForScreen and keeps it the size of the window.
        // And UI components own nothing on the GPU, so the whole screen can be put together right here in the
        // constructor. The compositor makes what it needs in its own Initialize, on the render thread.
        var compositor = AddComponent(UICompositor.ForScreen());
        compositor.DesignSize = DesignSize;
        module = compositor.CreateModule();

        var screen = module.AddComponent(new StackPanel { Spacing = 16 });

        var heading = screen.Add(new StackPanel { Anchor = Origin.Left, Spacing = 4 });
        heading.Add(new Label("Horizon examples") { Anchor = Origin.Left, TextScale = 0.6f });
        heading.Add(new Label($"{tests.Count(test => test.Level != TestLevel.Checks)} of them, simplest first. Each one is a scene you can read, and only leans on the ones above it.")
        {
            Anchor = Origin.Left,
            TextScale = 0.25f,
            Color = HostStyle.Dim
        });

        var body = screen.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 20 });

        /* The list */

        var listCard = body.Add(new StackPanel { Color = HostStyle.Card, Radius = HostStyle.RADIUS, Padding = new UIEdges(16) });

        // A scroll panel has to be told how tall it is. It never sizes itself to what's in it (then there'd be
        // nothing to scroll), so left to itself it comes out flat. One notch of the wheel moves it a row.
        var list = listCard.Add(new ScrollPanel
        {
            Size = new Vector2(LIST_WIDTH + 24, BODY_HEIGHT),
            WheelStep = ROW_HEIGHT + ROW_SPACING
        });

        // The padding on the right keeps the rows off the scroll bar
        var column = list.Add(new StackPanel { Spacing = ROW_SPACING, Padding = new UIEdges(0, 0, 24, 0) });

        foreach (TestLevel level in Enum.GetValues<TestLevel>())
        {
            // A level with nothing in it yet doesn't get a heading
            var inLevel = tests.Where(test => test.Level == level).ToArray();
            if (inLevel.Length == 0)
                continue;

            // Taller than its text with the text at the bottom, which is what leaves a gap above every heading
            column.Add(new Label(TestCatalog.NameOf(level))
            {
                Anchor = Origin.Left,
                Align = Origin.BottomLeft,
                Size = new Vector2(LIST_WIDTH, column.Children.Count == 0 ? 30 : 48),
                TextScale = 0.36f,
                Color = HostStyle.Accent
            });
            column.Add(new Label(TestCatalog.BlurbOf(level))
            {
                Anchor = Origin.Left,
                Align = Origin.Left,
                Size = new Vector2(LIST_WIDTH, 22),
                TextScale = 0.2f,
                Color = HostStyle.Dim
            });

            foreach (var test in inLevel)
            {
                // Pressed on the simulation thread (that's where the UI is updated). Start is fine with that, the
                // actual switch waits for the start of the next frame.
                var row = column.Add(new Button(test.Number > 0 ? $"{test.Number:00}   {test.Name}" : test.Name)
                {
                    Style = "button_flat",
                    Size = new Vector2(LIST_WIDTH, ROW_HEIGHT),
                    LabelScale = 0.26f,
                    OnPressed = () => host.Start(test)
                });
                rows[row] = test;
            }
        }

        /* What there is to say about the one that is picked */

        var details = body.Add(new StackPanel
        {
            Anchor = Origin.Top,
            Color = HostStyle.Card,
            Radius = HostStyle.RADIUS,
            Padding = new UIEdges(32, 28),
            Spacing = 12,
            Size = new Vector2(DETAILS_WIDTH + 64, BODY_HEIGHT + 32)
        });

        Label Line(float scale, Vector4? colour = null, bool wraps = false) => details.Add(new Label
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = scale,
            Color = colour,
            Wrap = wraps,

            // A label only wraps when it knows how wide it may be
            Size = wraps ? new Vector2(DETAILS_WIDTH, 0) : Vector2.Zero
        });

        where = Line(0.22f, HostStyle.Dim);
        name = Line(0.5f);
        summary = Line(0.27f, wraps: true);

        details.Add(new Spacer { Space = 4 });
        details.Add(new Divider { Fill = UIFill.Horizontal });
        Line(0.24f, HostStyle.Accent).Text = "What to look for in the code";
        shows = Line(0.24f, wraps: true);

        details.Add(new Spacer { Space = 4 });
        details.Add(new Divider { Fill = UIFill.Horizontal });
        Line(0.24f, HostStyle.Accent).Text = "Where it is";
        source = Line(0.22f, wraps: true);
        command = Line(0.22f, HostStyle.Dim, wraps: true);

        details.Add(new Spacer { Space = 8 });
        run = details.Add(new Button("Run it")
        {
            Anchor = Origin.Left,
            Size = new Vector2(220, 54),
            LabelScale = 0.3f
        });

        screen.Add(new Label("Up and Down or the mouse to pick, Enter or a click to run. Esc comes back here from an example, Page Up and Page Down flick between them.")
        {
            Anchor = Origin.Left,
            TextScale = 0.22f,
            Color = HostStyle.Dim
        });

        Show(tests[0]);
    }

    public override void PostInit()
    {
        base.PostInit();

        // Something is selected from the start, which is what lets the arrow keys take over without a click first
        module.Navigation.SelectFirst();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        // The details follow the pointer while it is on a row, and the navigator's selection the rest of the time
        TestDefinition? picked = null;
        foreach (var (row, test) in rows)
        {
            if (row.IsHovered) picked = test;
        }

        if (picked is null && module.Navigation.Current is { } current && rows.TryGetValue(current, out var selected))
            picked = selected;

        if (picked is not null)
            Show(picked);
    }

    public override void Render(float dt)
    {
        // Render thread. Every frame rather than once, whatever example you just left has probably buggered about with it
        Engine.Graphics.ClearColor = HostStyle.Backdrop;
        base.Render(dt);
    }

    /// <summary>Helper method to fill the right hand side in for an example. Simulation thread, like everything that touches the UI.</summary>
    private void Show(TestDefinition test)
    {
        if (ReferenceEquals(test, shown)) return;
        shown = test;

        where.Text = test.Number > 0 ? $"{TestCatalog.NameOf(test.Level)}, number {test.Number}" : "A check, it runs by itself";
        name.Text = test.Name;
        summary.Text = test.Summary;
        shows.Text = string.Join("     ", test.Shows);
        source.Text = $"Horizon.Testing/{test.Source}";
        command.Text = $"Start straight in it with   Horizon.Testing {test.Id}";
        run.OnPressed = () => host.Start(test);
    }
}
