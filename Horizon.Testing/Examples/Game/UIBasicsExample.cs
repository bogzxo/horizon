using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.Game;

/// <summary>
/// The first UI. Two bars pinned to the top corners of the screen like a HUD, and the same little panel twice,
/// once put together in C# and once written as a script in a file, so you can read the two next to each other and
/// pick whichever suits. The line at the bottom says whether the pointer is over the UI or over the game, which is
/// the thing a game needs to know before it treats a click as a shot.
/// <para>
/// What to look at. <see cref="UICompositor"/> (a UI, added to a scene as a component) and its
/// <see cref="UIModule"/>s (a piece of UI that is laid out, moved and scaled as one),
/// <see cref="UIComponent.Anchor"/> and <see cref="UIComponent.Position"/> for pinning something to a corner,
/// <see cref="StackPanel"/> for things one after the other, <see cref="Button.OnPressed"/> and friends,
/// <see cref="UIModule.LoadLayout"/> with <see cref="UILayout.Get{T}"/> for a UI out of a file, and
/// <see cref="UICompositor.IsPointerOverUI"/>.
/// </para>
/// <para>
/// In your own game.
/// <code>
/// // Constructor, UI components own nothing on the GPU so the whole lot can be made right there
/// ui = AddComponent(UICompositor.ForScreen());
/// var hud = ui.CreateModule();
/// health = hud.AddComponent(new ProgressBar { Anchor = Origin.TopLeft, Position = new Vector2(24, -48) });
///
/// var menu = ui.CreateModule().LoadLayout("Assets/ui/menu.hor");
/// menu.Get&lt;Button&gt;("play").OnPressed = StartGame;       // simulation thread, like everything about the UI
///
/// // UpdateState
/// if (mouse.WasPressed(MouseButton.Left) &amp;&amp; !ui.IsPointerOverUI) Shoot();
/// </code>
/// </para>
/// </summary>
public class UIBasicsExample : Scene, ITestControls
{
    private const string SCRIPTED = "Assets/examples/ui/scripted_panel.hor";

    private static readonly Vector4 PanelColour = new(0.1f, 0.12f, 0.17f, 0.92f);

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Mouse", "press, drag"),
        new("Space", "scale the scripted panel")
    ];

    private readonly UICompositor _ui;
    private readonly UIModule _scripted;
    private readonly ProgressBar _health, _stamina;
    private readonly Label _status;

    public UIBasicsExample()
    {
        // A UI is drawn through a camera, and what that camera sees is the screen it is laid out against.
        // UICompositor.ForScreen() makes one the size of the window by itself, this is the long way round
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;
        _ui = AddComponent(new UICompositor(camera));

        /* A HUD, anchored to the screen rather than placed by hand */

        // Anchor says which corner of the screen a thing is measured from, Position how far from it. Y goes up,
        // so down from the top is a minus. Resize the window and they stay in their corners
        var hud = _ui.CreateModule();
        _health = hud.AddComponent(new ProgressBar { Anchor = Origin.TopLeft, Position = new Vector2(24, -48), Progress = 0.75f });
        _stamina = hud.AddComponent(new ProgressBar { Anchor = Origin.TopRight, Position = new Vector2(-24, -48), Progress = 0.4f });
        _status = hud.AddComponent(new Label { Anchor = Origin.Bottom, Position = new Vector2(0, 84), TextScale = 0.3f });

        /* The panel as a script, out of a file */

        // A module is moved as a whole, everything in it comes along
        _scripted = _ui.CreateModule();
        _scripted.Position = new Vector2(-300, 0);
        _scripted.LoadLayout(SCRIPTED);

        /* And the same panel in C# */

        var native = _ui.CreateModule();
        native.Position = new Vector2(300, 0);

        var panel = native.AddComponent(new StackPanel
        {
            Color = PanelColour,
            Radius = 10.0f,
            Padding = new UIEdges(24),
            Spacing = 16,

            // Everything in it as wide as the widest thing in it
            Stretch = true
        });

        panel.Add(new Label("Built in C#") { TextScale = 0.45f });

        // Pressed on the simulation thread, which is where the UI is updated, so a handler can touch the game
        panel.Add(new Button("Hurt") { OnPressed = () => _health.Progress -= 0.25f });
        panel.Add(new Button("Heal") { OnPressed = () => _health.Progress += 0.25f });
        panel.Add(new Button("Locked") { Enabled = false });

        var bars = panel.Add(new ToggleButton("Show the HUD bars", startingState: true));
        bars.OnPressed = () => _health.Visible = _stamina.Visible = bars.State;
    }

    public override void PostInit()
    {
        base.PostInit();
        Engine.Graphics.ClearColor = new Vector4(0.22f, 0.27f, 0.36f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _status.Text = _ui.IsPointerOverUI ? "The pointer is over the UI" : "The pointer is over the game";

        // A scaled module is still pressed where it is drawn
        if (Engine.Input.Keyboard.WasPressed(Key.Space))
            _scripted.Scale = _scripted.Scale.X >= 1.5f ? Vector2.One : _scripted.Scale + new Vector2(0.25f);
    }
}
