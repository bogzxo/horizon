using Horizon.Rendering;

using Horizon.Engine;

using Silk.NET.Input;

namespace Horizon.UI;

/// <summary>How much a <see cref="PerformanceOverlay"/> shows.</summary>
public enum PerformanceDetail
{
    /// <summary>Nothing, it's not there (and costs next to nothing).</summary>
    Off,

    /// <summary>
    /// The minimal one, a pill in a corner. Frames a second, how long a frame takes on the CPU and on the GPU, and
    /// the last few seconds of frames as a little graph. For players who like a number.
    /// </summary>
    Compact,

    /// <summary>
    /// The advanced one, a card. The frames of the last twelve seconds and how uneven they were, every thread of the
    /// engine and what it is doing, what every pass of the frame costs the GPU, what the frame asked of the device,
    /// memory and garbage. For you.
    /// </summary>
    Full,
}

/// <summary>
/// The engine's vital signs drawn over the game, straight out of the loops and the device. Add one to the engine and
/// it is there, the minimal one to begin with, with the key that gets the rest on a key cap next to it.
/// <code>
/// engine.AddEntity(new PerformanceOverlay());                          // the minimal one, F3 for the lot
/// engine.AddEntity(new PerformanceOverlay(PerformanceDetail.Off));     // not there until F3
/// </code>
/// F3 goes Off, Compact, Full and round again (<see cref="ToggleKey"/>, null for no key, then set <see cref="Detail"/>
/// yourself, from an options screen say). It sits on a screen UI of its own over everything else, drawn in the plain
/// flat skin so it reads the same in every game, and is one component that paints itself (<see cref="PerformanceBoard"/>)
/// out of what was last read off the engine (<see cref="PerformanceSample"/>), four times a second for the numbers
/// and ten for the graphs. None of that makes any garbage, what it says about the garbage is the game's.
/// <para>
/// Reading the card, from the top. The frame rate, white while it is what it is meant to be (the limit on the
/// frames, or sixty), and what a frame costs the CPU and the GPU with a bar each of how much of the frame they are
/// busy for, the one that is full is what the frames are waiting on. The frame time graph is the last twelve
/// seconds, the solid part of a bar what a frame took on average in that tenth of a second and the pale part on
/// top its worst, so a hitch is a spike that stays up there for twelve seconds however fast the frames come. A
/// frame rate is only as good as its worst frames, so the slowest in a hundred, the worst of the lot and the
/// stutters (frames over twice the budget) have numbers under it.
/// </para>
/// <para>
/// The simulation should sit on its target rate (120 by default) whatever the frames are doing, that's the whole
/// point of drawing alongside it. Its graph is what a tick cost against what it may cost, the dashed line. "Behind"
/// is how far in the past the frames show, about a tick and a bit, "dropped" ticks mean the simulation couldn't
/// keep up and let some go, which turns red while it is happening. The GPU's bar is the frame cut up by its passes,
/// the stretches the renderers name (see <see cref="Graphics.GraphicsDevice.BeginGpuScope"/>), a frame or two
/// behind the CPU. Garbage that never stops climbing is what ends up as a hitch when the collector has to clean up
/// after it.
/// </para>
/// </summary>
public sealed class PerformanceOverlay : GameObject
{
    // How often the numbers are written out and how often the graphs move on. Numbers that change every update
    // are a blur nobody can read, a graph is another matter and gets a slice of its timeline at a time
    private const float REFRESH = 0.25f;
    private const float GRAPH_REFRESH = 0.1f;

    // How far from the edges of the screen it sits
    private const float MARGIN = 16.0f;

    private const string SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string SKIN_FILE = "skin.hor";

    private readonly PerformanceSample sample = new();

    private UICompositor ui = null!;
    private UIModule module = null!;
    private PerformanceBoard board = null!;

    private float refresh, graphRefresh;

    // What it showed and which key was on its cap as of the last update, to tell when either changes
    private PerformanceDetail shown = PerformanceDetail.Off;
    private Key? capped;

    /// <summary>How much is shown, see <see cref="PerformanceDetail"/>. From any thread.</summary>
    public PerformanceDetail Detail { get; set; }

    /// <summary>The key that goes round the levels of detail, null for none.</summary>
    public Key? ToggleKey { get; set; } = Key.F3;

    /// <summary>Which corner of the screen it sits in.</summary>
    public Origin Corner { get; set; } = Origin.TopRight;

    /// <summary>How big it's drawn, a unit per pixel at 1.</summary>
    public float Scale { get; set; } = 1.0f;

    public PerformanceOverlay(PerformanceDetail detail = PerformanceDetail.Compact)
    {
        Name = "Performance Overlay";
        Detail = detail;
    }

    public override void Initialize()
    {
        // It sits on the engine and outlives every scene, so what it makes on the GPU is nobody's. Left to itself
        // its skin and its buffers would be noted down as the scene's that happened to be up when they were made
        // and go with it, and the overlay would be drawing out of a texture that isn't there
        using var shared = Horizon.Content.AssetScope.EnterGlobal();

        base.Initialize();

        // A UI of its own with a camera of its own, so it doesn't care what the game's cameras are up to
        ui = AddComponent(UICompositor.ForScreen(SKIN_DIRECTORY, SKIN_FILE));
        module = ui.CreateModule();
        module.ShowInLayoutDebugger = false;

        board = module.AddComponent(new PerformanceBoard(sample));
    }

    public override void UpdateState(float dt)
    {
        if (ToggleKey is { } key && Engine.Input.Keyboard.WasPressed(key))
            Detail = (PerformanceDetail)(((int)Detail + 1) % 3);

        PerformanceDetail detail = Detail;
        module.Enabled = detail != PerformanceDetail.Off;

        if (detail != PerformanceDetail.Off)
        {
            ui.Scale = Scale;

            // Tucked into its corner, a bit off the edges
            board.Anchor = Corner;
            board.Position = Corner.ToVector() * (-2.0f * MARGIN);
            board.Detail = detail;

            // As tall as the screen has room for, a card that doesn't fit down the side goes into two columns
            float scale = ui.UIScale > 0.0f ? ui.UIScale : 1.0f;
            board.MaxHeight = ui.viewportCamera.Bounds.Height / scale - MARGIN * 2.0f;

            // The name of a key is a string, made when the key changes and not every update
            if (ToggleKey != capped)
            {
                capped = ToggleKey;
                board.Key = capped?.ToString() ?? string.Empty;
            }

            // Another level of detail is read at once, there is nothing sadder than a card of noughts, and it
            // slides in from the corner it lives in rather than just being there
            bool changed = detail != shown;
            if (changed)
            {
                refresh = graphRefresh = 0.0f;
                board.Reset();
                board.SlideIn(Corner.ToVector() * 28.0f, 0.22f);
            }

            if ((refresh -= dt) <= 0.0f)
            {
                refresh = REFRESH;
                graphRefresh = GRAPH_REFRESH;
                sample.Read(Engine);
            }
            else if ((graphRefresh -= dt) <= 0.0f)
            {
                graphRefresh = GRAPH_REFRESH;
                sample.ReadTimelines();
            }
        }

        shown = detail;

        // The UI of the overlay goes along with it, off or on
        base.UpdateState(dt);
    }

    public override void Render(float dt)
    {
        // The same as in Initialize, a skin stitches its atlas together as it is first drawn and that is here
        using var shared = Horizon.Content.AssetScope.EnterGlobal();
        base.Render(dt);
    }
}
