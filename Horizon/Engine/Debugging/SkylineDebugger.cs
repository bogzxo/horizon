using System.Numerics;

using Horizon.Content;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine.Components;
using Horizon.Graphics;
using Horizon.Graphics.Vulkan;
using Horizon.Logging;
using Horizon.Physics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Engine.Debugging;

/// <summary>
/// The Skyline debugger, what a Debug build of any game made with Horizon has over it without asking. A menu bar
/// along the top of the window, and behind it the tools for looking into a running game and tuning it.
/// <list type="bullet">
/// <item>The scene tree, every entity the engine is running and the components on each (<see cref="SceneTreeView"/>).</item>
/// <item>The inspector, the fields of whatever is selected, changed while the game runs (<see cref="InspectorView"/>,
/// and <see cref="InspectAttribute"/> for getting something private in there).</item>
/// <item>The content drawer, the textures, render targets, shaders and buffers that are loaded (<see cref="ContentView"/>).</item>
/// <item>The metrics, what every pass of the frame costs (<see cref="MetricsView"/>), and the log.</item>
/// <item>Pausing the scene, stepping it a tick at a time and slowing it down (<see cref="SceneManager.Paused"/>).</item>
/// </list>
/// F10 shows and hides the lot. Hidden there is nothing of it on screen, not the bar either, and the game has the
/// window. Shown, the game is in a container in the middle with the panels docked around it, the way an engine
/// with an editor has it, and nothing the game draws gets anywhere else. The game is still drawn at the size of
/// the window and then shown smaller, so what it costs is what it costs without any of this, and the mouse is
/// handed to it as if its container were the whole window. F8 pauses, F9 steps. <c>HORIZON_DEBUGGER=on</c> in
/// the environment has it up from the start.
/// <para>
/// None of it is in a Release build, the folder isn't compiled (see Horizon.csproj). The engine adds one of these
/// to itself in Debug and it is at <see cref="GameEngine.Debugger"/>.
/// </para>
/// </summary>
public sealed partial class SkylineDebugger : GameObject
{
    /// <summary>How it starts, off (not shown until F10, which is what it is without this) or on.</summary>
    public const string ENVIRONMENT_VARIABLE = "HORIZON_DEBUGGER";

    private const string SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string SKIN_FILE = "skin.hor";

    // How long something said in the bar stays there
    private const float SAY_TIME = 5.0f;

    // How many frames a picture that was the wrong size is kept, whatever was painted with it has been drawn by then
    private const int RETIRE_FRAMES = 8;

    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 1.0f, 2.0f];

    private UICompositor ui = null!;
    private UIModule module = null!;
    private DebugDock dock = null!;
    private MenuBar menus = null!;
    private Label status = null!, numbers = null!, hint = null!;
    private Button pause = null!, step = null!;
    private SceneTreeView tree = null!;
    private InspectorView inspector = null!;
    private LogView log = null!;

    private float sayTimer, numbersTimer;

    // The frame of the game as it was drawn, for the container. Made and filled on the render thread, read by the
    // UI as it paints, and one that is the wrong size is kept a few frames before it goes
    private RenderTarget? picture;
    private volatile Texture? pictureTexture;
    private readonly List<(RenderTarget Target, int Frames)> retired = [];
    private volatile bool capturing;
    private bool drawing;

    /// <summary>
    /// Whether it is on screen, the bar, the panels and the game in its container between them. Off there is
    /// nothing of it to be seen and the game has the whole window. From any thread.
    /// </summary>
    public bool Shown { get; set; }

    /// <summary>The key that shows and hides the suite. Null for none.</summary>
    public Key? ToggleKey { get; set; } = Key.F10;

    public Key? PauseKey { get; set; } = Key.F8;

    public Key? StepKey { get; set; } = Key.F9;

    /// <summary>What the inspector is showing, an entity, a component, a texture, anything at all. Null for nothing.</summary>
    public object? Selected { get; private set; }

    internal UISkin? Skin => ui?.Skin;

    public SkylineDebugger()
    {
        Name = "Skyline Debugger";

        switch (Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE)?.Trim().ToLowerInvariant())
        {
            case "on" or "1" or "editor":
                Shown = true;
                break;
        }
    }

    public override void Initialize()
    {
        // It outlives every scene, what it makes on the GPU is nobody's, see the performance overlay
        using var shared = AssetScope.EnterGlobal();

        base.Initialize();

        ui = AddComponent(UICompositor.ForScreen(SKIN_DIRECTORY, SKIN_FILE));

        // The game may be looking at the mouse through its container, the suite never is
        ui.PointerSource = ReadPointer;
        ui.ReadsWindowMouse = true;

        module = ui.CreateModule();
        module.ShowInLayoutDebugger = false;

        var bar = new Panel { Color = DebugStyle.Title };
        // Its name on the door, then the menus
        var lead = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 10.0f, Anchor = Origin.Left, Position = new Vector2(12.0f, 0.0f) });
        lead.Add(new Label("SKYLINE") { Color = new Vector4(0.38f, 0.62f, 1.0f, 1.0f), Tooltip = "the Skyline debugger of Horizon, F10 puts it away" });
        menus = lead.Add(new MenuBar());

        var tools = bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6.0f, Anchor = Origin.Right, Position = new Vector2(-8.0f, 0.0f) });
        status = tools.Add(new Label { Align = Origin.Right, Color = DebugStyle.Dim });
        tools.Add(new Spacer { Size = new Vector2(8.0f, 1.0f) });
        pause = tools.Add(new Button("Pause") { Size = new Vector2(76.0f, 22.0f), Tooltip = "stops the scene where it is, F8", OnPressed = TogglePause });
        step = tools.Add(new Button("Step") { Size = new Vector2(56.0f, 22.0f), Tooltip = "one tick of a paused scene, F9", OnPressed = StepOnce });
        tools.Add(new Spacer { Size = new Vector2(8.0f, 1.0f) });
        numbers = tools.Add(new Label { Align = Origin.Right });

        // The key that puts all of this away, in yellow like the one on the performance overlay
        tools.Add(new Spacer { Size = new Vector2(4.0f, 1.0f) });
        hint = tools.Add(new Label { Align = Origin.Right, Color = DebugStyle.Warn });

        tree = new SceneTreeView(this);
        inspector = new InspectorView(this);
        log = new LogView();

        var left = new DockPanel { Titles = ["Scene"] };
        left.Add(new ScrollPanel { Padding = new UIEdges(2.0f, 4.0f) }).Add(tree);

        var right = new DockPanel { Titles = ["Inspector"] };
        right.Add(new ScrollPanel()).Add(inspector);

        var bottom = new DockPanel { Titles = ["Content", "Metrics", "Log"] };
        bottom.Add(new ContentView(this));
        bottom.Add(new ScrollPanel()).Add(new MetricsView());
        bottom.Add(new ScrollPanel()).Add(log);

        dock = module.AddComponent(new DebugDock
        {
            Fill = UIFill.Both,
            Bar = bar,
            Left = left,
            Right = right,
            Bottom = bottom,
            Game = new GameView { Picture = () => pictureTexture }
        });

        dock.Add(bar);
        dock.Add(left);
        dock.Add(right);
        dock.Add(bottom);
        dock.Add(dock.Game);

        BuildMenus();

        Log.Written += log.Hear;
    }

    protected override void DisposeOther()
    {
        if (log is not null)
            Log.Written -= log.Hear;

        picture?.Dispose();
        foreach (var (target, _) in retired)
            target.Dispose();
    }

    /* What the menus, the buttons and the keys do */

    /// <summary>Shows something in the inspector, anything with fields. Brings the suite up if it isn't.</summary>
    public void Select(object? target) => Select(target, reveal: true);

    internal void Select(object? target, bool reveal)
    {
        Selected = target;
        inspector.Show(target);

        if (!reveal || target is null)
            return;

        Shown = true;
        dock.ShowRight = true;
        if (target is Entity or IGameComponent)
        {
            dock.ShowLeft = true;
            tree.Reveal(target);
        }
    }

    /// <summary>Writes a line into the menu bar that goes away by itself, for what a tool has to say.</summary>
    public void Say(string message, bool error = false)
    {
        if (status is null)
            return;

        status.Text = DebugStyle.Fit(Skin, message, 520.0f);
        status.Color = error ? DebugStyle.Bad : DebugStyle.Dim;
        sayTimer = SAY_TIME;
    }

    private void TogglePause()
    {
        var scenes = Engine.SceneManager;
        scenes.Paused = !scenes.Paused;
        Say(scenes.Paused ? "the scene stands still, F9 for a tick of it" : "the scene runs again");
    }

    private void StepOnce()
    {
        var scenes = Engine.SceneManager;
        if (!scenes.Paused)
            scenes.Paused = true;

        scenes.Step();
    }

    /// <summary>Helper method to do something to every component of a kind there is, under the engine and in the scene that is on screen.</summary>
    private void Each<T>(Action<T> action) where T : class
    {
        void Visit(Entity entity)
        {
            if (ReferenceEquals(entity, this))
                return;

            if (entity is T self) action(self);

            foreach (IGameComponent component in entity.Components)
            {
                if (component is T found) action(found);
            }

            foreach (Entity child in entity.Children)
                Visit(child);

            if (entity is SceneManager { CurrentInstance: { } scene })
                Visit(scene);
        }

        Visit(Engine);
    }

    /* The mouse, the keys and the layout, once an update and before the scene has its turn */

    private UIPointer ReadPointer()
    {
        var mouse = Engine.Input.Mouse;
        return new UIPointer(
            ui.viewportCamera.ScreenToWorld(mouse.WindowPosition),
            mouse.HeldInWindow(MouseButton.Left) || mouse.PressedInWindow(MouseButton.Left),
            mouse.HeldInWindow(MouseButton.Right) || mouse.PressedInWindow(MouseButton.Right));
    }

    public override void UpdateState(float dt)
    {
        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;
        bool typing = ui.Focus is not null;

        // The keys of the suite are heard whether the game gets the keyboard or not, only not in the middle of a word
        if (ToggleKey is { } toggle && keyboard.PressedRegardless(toggle))
            Shown = !Shown;

        if (!typing && PauseKey is { } pauseKey && keyboard.PressedRegardless(pauseKey)) TogglePause();
        if (!typing && StepKey is { } stepKey && keyboard.PressedRegardless(stepKey)) StepOnce();

        // Escape lets go of whatever box was being typed into, the compositor can't hear it while the keys are withheld
        if (typing && keyboard.PressedRegardless(Key.Escape))
            ui.Focus = null;

        bool shown = Shown;

        module.Enabled = shown;
        dock.Sync();

        // The picture in the container changes every frame without a quad of the UI changing, so nothing of this
        // UI is kept from one frame to the next
        ui.Retained = false;

        if (shown)
            WriteBar(dt);

        base.UpdateState(dt);

        // What the game gets to see of the mouse and the keys this update. In its container the pointer is where
        // it would be if the container were the window, and over a panel the buttons are the suite's
        Vector2 window = Engine.WindowManager.WindowSize;
        UIRect screen = dock.Bounds, game = dock.GameRect;
        if (shown && !screen.IsEmpty && !game.IsEmpty)
        {
            mouse.ViewOrigin = new Vector2((game.Min.X - screen.Min.X) / screen.Width, (screen.Max.Y - game.Max.Y) / screen.Height) * window;
            mouse.ViewScale = new Vector2(screen.Width / game.Width, screen.Height / game.Height);
        }
        else
        {
            mouse.ViewOrigin = Vector2.Zero;
            mouse.ViewScale = Vector2.One;
        }

        // Over a panel the buttons are the suite's, and so is everything outside of the container, a click on the
        // dark around the game is not a click in the game
        mouse.Withheld = shown && (ui.IsPointerOverUI || !game.Contains(module.ToLocal(ui.Pointer.Position)));
        keyboard.Withheld = shown && ui.Focus is not null;
        capturing = shown;
    }

    private void WriteBar(float dt)
    {
        var scenes = Engine.SceneManager;
        pause.Label = scenes.Paused ? "Resume" : "Pause";
        pause.Selected = scenes.Paused;
        hint.Text = ToggleKey?.ToString() ?? string.Empty;

        if (sayTimer > 0.0f && (sayTimer -= dt) <= 0.0f)
            status.Text = string.Empty;

        if ((numbersTimer -= dt) > 0.0f)
            return;

        numbersTimer = 0.25f;

        LoopStatistics? render = null;
        foreach (var loop in Engine.WindowManager.Loops)
        {
            if (loop.Name == "Render") render = loop;
        }

        string speed = scenes.Paused ? "paused   " : scenes.TimeScale != 1.0f ? $"{scenes.TimeScale:0.##}x   " : string.Empty;
        numbers.Text = render is null ? speed : $"{speed}{render.Rate:0} fps   {render.WorkMs:0.0} ms cpu   {Engine.GpuFrameMs:0.0} ms gpu";

        Vector2 size = Engine.WindowManager.ViewportSize;
        float shown = dock.Bounds.Width > 0.0f ? dock.GameRect.Width / dock.Bounds.Width : 1.0f;
        string scene = Engine.Scene is { } current ? current.GetType().Name : "no scene";
        dock.Caption = $"{scene}   {size.X:0} x {size.Y:0}, shown at {shown:0%}";
    }

    /* Drawing, which is the last thing in the frame */

    /// <summary>
    /// Whether a texture can be put on screen as a picture. It has to be there, be read through a sampler at all
    /// and hold colours, a depth buffer or a grid of whole numbers is not something the sprite shader reads.
    /// </summary>
    internal static bool CanShow(Texture texture) =>
        texture.IsValid &&
        texture.BindlessIndex != BindlessTextures.NONE &&
        (texture.Definition.Usage & TextureUsage.Sampled) != 0 &&
        texture.Definition.Format is not (PixelFormat.Rg32Uint or PixelFormat.Depth24Stencil8 or PixelFormat.Depth32F);

    public override void Render(float dt)
    {
        // Not in its place among the children of the engine, see Draw
        if (drawing)
            base.Render(dt);
    }

    /// <summary>
    /// Draws the suite over the frame, called by the engine once everything else is in it. The frame as it is at
    /// that moment is copied off the window first, which is the picture the container shows, and the suite then
    /// paints over all of the window with that picture in the middle, so whatever the game drew and wherever it
    /// drew it, the container is the only place it is seen. Hidden, none of that happens. Render thread.
    /// </summary>
    internal void Draw(float dt)
    {
        if (!IsInitialized || !Enabled)
            return;

        using var shared = AssetScope.EnterGlobal();

        for (int i = retired.Count - 1; i >= 0; i--)
        {
            if (retired[i].Frames > 0)
            {
                retired[i] = (retired[i].Target, retired[i].Frames - 1);
                continue;
            }

            retired[i].Target.Dispose();
            retired.RemoveAt(i);
        }

        if (capturing)
            TakePicture();

        drawing = true;
        try
        {
            Render(dt);
        }
        finally
        {
            drawing = false;
        }
    }

    private void TakePicture()
    {
        Vector2 size = Engine.WindowManager.ViewportSize;
        uint width = (uint)size.X, height = (uint)size.Y;
        if (width == 0 || height == 0)
            return;

        if (picture is null || picture.Width != width || picture.Height != height)
        {
            // Whatever was painted with the old one may still be on its way to the screen
            if (picture is not null)
                retired.Add((picture, RETIRE_FRAMES));

            picture = RenderTarget.Create(RenderTargetDescription.Color(width, height, TextureDefinition.RgbaUnsignedByte));
            picture.Name = "debugger game picture";
        }

        var graphics = Engine.Graphics;
        using (graphics.BeginGpuScope("debugger copy"))
            graphics.CopyWindow(picture, width, height, width, height);

        graphics.BindWindow();
        pictureTexture = picture.Color;
    }
}
