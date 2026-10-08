using System.Drawing;
using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Testing;

/// <summary>
/// The menu you land on: every test under the heading of its <see cref="TestArea"/>, a button each with a line about
/// it next to it. Pressing one hands it to the <see cref="TestHost"/> to run.
/// <para>
/// It's a UI and nothing else, so it's a decent little example of UIX built straight from C# too: a
/// <see cref="UICompositor.ForScreen"/> UI laid out for a <see cref="UICompositor.DesignSize"/>, stacks inside stacks,
/// and a <see cref="ScrollPanel"/> for a list that's taller than the screen.
/// </para>
/// </summary>
internal sealed class TestSelectorScene : Scene
{
    // Laid out for 1600 by 900. A smaller window gets the whole lot shrunk to fit, a bigger one gets it bigger, and
    // either way nothing falls off the edge
    private static readonly Vector2 DesignSize = new(1600, 900);

    // The list scrolls inside this much of the screen, which leaves room for the title and the hint at the bottom
    private const float ListHeight = 620.0f;

    private const float RowHeight = 52.0f;
    private const float RowSpacing = 10.0f;
    private const float HeadingHeight = 64.0f;

    private static readonly Vector4 PanelColor = new(0.1f, 0.12f, 0.17f, 0.92f);
    private static readonly Vector4 HeadingColor = new(1.0f, 0.8f, 0.45f, 1.0f);
    private static readonly Vector4 DimColor = new(0.93f, 0.95f, 1.0f, 0.6f);

    // The host keeps the selector, so it comes back exactly as you left it, scrolled to wherever you were
    public override bool Persistent => true;

    public TestSelectorScene(TestHost host, IReadOnlyList<TestDefinition> tests)
    {
        // No ActiveCamera: there's nothing in the world to look at, and a scene that doesn't set one is seen through
        // the engine's. The UI brings its own camera with ForScreen and keeps it the size of the window.
        // And UI components own nothing on the GPU, so the whole screen can be put together right here in the
        // constructor. The compositor makes what it needs in its own Initialize, on the render thread.
        var compositor = AddComponent(UICompositor.ForScreen());
        compositor.DesignSize = DesignSize;

        var panel = compositor.CreateModule().AddComponent(new StackPanel
        {
            Color = PanelColor,
            Padding = new UIEdges(32, 28),
            Spacing = 16,
            Background = "panel"
        });

        panel.Add(new Label("Horizon examples") { TextScale = 0.6f });
        panel.Add(new Label("New here? Start with Basics and work your way down. Every one is a scene in Examples/.")
        {
            TextScale = 0.25f,
            Color = DimColor
        });

        // A scroll panel has to be told how tall it is. It never sizes itself to what's in it (then there'd be
        // nothing to scroll), so left to itself it comes out flat. One notch of the wheel moves it a row.
        var list = panel.Add(new ScrollPanel
        {
            Size = new Vector2(0, ListHeight),
            WheelStep = RowHeight + RowSpacing
        });

        // Two columns sharing their row heights, so every description lines up with its button and the buttons all
        // come out as wide as the widest of them. The headings sit in the same columns: the area's name on the
        // left, a line about it on the right. The padding on the right keeps the longest line off the scroll bar.
        var columns = list.Add(new StackPanel
        {
            Direction = UIDirection.Horizontal,
            Spacing = 20,
            Padding = new UIEdges(0, 0, 24, 0)
        });
        var names = columns.Add(new StackPanel { Stretch = true, Spacing = RowSpacing });
        var descriptions = columns.Add(new StackPanel { Spacing = RowSpacing });

        foreach (TestArea area in Enum.GetValues<TestArea>())
        {
            // An area with nothing in it yet doesn't get a heading
            var inArea = tests.Where(test => test.Area == area).ToArray();
            if (inArea.Length == 0)
                continue;

            // Taller than a row with the text at the bottom, which is what leaves a gap above every heading
            names.Add(new Label(area.ToString())
            {
                Align = Origin.BottomLeft,
                Size = new Vector2(0, HeadingHeight),
                TextScale = 0.45f,
                Color = HeadingColor
            });
            descriptions.Add(new Label(Blurb(area))
            {
                Anchor = Origin.Left,
                Align = Origin.BottomLeft,
                Size = new Vector2(0, HeadingHeight),
                TextScale = 0.25f,
                Color = DimColor
            });

            foreach (var test in inArea)
            {
                // Pressed on the simulation thread (that's where the UI is updated). Start is fine with that, the
                // actual switch waits for the start of the next frame.
                names.Add(new Button(test.Name)
                {
                    Size = new Vector2(0, RowHeight),
                    OnPressed = () => host.Start(test)
                });
                descriptions.Add(new Label(test.Description)
                {
                    Anchor = Origin.Left,
                    Align = Origin.Left,
                    Size = new Vector2(0, RowHeight),
                    TextScale = 0.3f
                });
            }
        }

        panel.Add(new Label("Mouse wheel to scroll. ESC or Back gets you back here from a test.")
        {
            TextScale = 0.25f,
            Color = DimColor
        });
    }

    public override void Render(float dt)
    {
        // Render thread, so GL is fair game. Every frame rather than once: whatever test you just left has
        // probably buggered about with it
        Engine.Graphics.ClearColor = new Vector4(0.39f, 0.58f, 0.93f, 1.0f);
        base.Render(dt);
    }

    /// <summary>Helper method to say what an area is about, under its heading.</summary>
    private static string Blurb(TestArea area) => area switch
    {
        TestArea.Basics => "Start here. Entities, sprites and cameras, the stuff every game is made of.",
        TestArea.Input => "Keyboard, mouse and gamepads, read from the simulation thread.",
        TestArea.Rendering => "Everything that ends up as pixels and isn't a UI.",
        TestArea.UI => "UIX, from C# and out of layout files. The self-tests click through themselves.",
        TestArea.Physics => "Bodies, fixtures and particles that act like water.",
        TestArea.Engine => "The guts: threads, frame pacing, tweens and the like.",
        _ => string.Empty
    };
}
