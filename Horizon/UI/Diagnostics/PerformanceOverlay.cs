using Horizon.Rendering;
using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

using Silk.NET.Input;

namespace Horizon.UI;

/// <summary>How much a <see cref="PerformanceOverlay"/> shows.</summary>
public enum PerformanceDetail
{
    /// <summary>Nothing, it's not there (and costs next to nothing).</summary>
    Off,

    /// <summary>The minimal one. Frames a second, how long a frame takes on the CPU and on the GPU, in a corner. For players who like a number.</summary>
    Compact,

    /// <summary>
    /// The advanced one. Every thread of the engine and what it is doing, how uneven the frames come, what every
    /// pass of the frame costs the GPU, what the frame asked of the device, memory and garbage, and graphs. For you.
    /// </summary>
    Full,
}

/// <summary>
/// The engine's vital signs drawn over the game, straight out of the loops and the device. How fast frames are drawn
/// and how long they take on the CPU and the GPU, what every thread is doing (drawing, simulating, sleeping, held up
/// by another), how uneven the frames come (the slowest in a hundred, the stutters), what every pass of the frame
/// costs the GPU, how many draws and dispatches a frame is, how much memory the GPU holds, how far in the past frames
/// are drawn so they can be interpolated, ticks that got dropped, and how much garbage every thread is making. Add
/// one to the engine and press F3.
/// <code>
/// engine.AddEntity(new PerformanceOverlay());                          // off until F3
/// engine.AddEntity(new PerformanceOverlay(PerformanceDetail.Full));    // on from the start
/// </code>
/// F3 goes Off, Compact, Full and round again (<see cref="ToggleKey"/>, null for no key, then set <see cref="Detail"/>
/// yourself, from an options screen say). It's a UIX layout like any other, on a screen UI of its own over everything
/// else, drawn in the plain flat skin so it reads the same in every game.
/// <para>
/// Reading it. The simulation should sit on its target rate (120 by default) whatever the frames are doing, that's
/// the whole point of drawing alongside it. "Behind" is how far in the past the frames show, about a tick and a bit,
/// "dropped" ticks mean the simulation couldn't keep up and let some go. A frame rate is only as good as its worst
/// frames, so the slowest one in a hundred and the stutters (frames over twice the budget) are on their own line.
/// The GPU passes are the stretches of the frame the renderers name (see <see cref="Graphics.GraphicsDevice.BeginGpuScope"/>),
/// a frame or two behind the CPU. Garbage that never stops climbing is what ends up as a hitch when the collector
/// has to clean up after it.
/// </para>
/// </summary>
public sealed class PerformanceOverlay : GameObject
{
    // How often the numbers are written out. Every update would be a blur nobody can read, and garbage of its own
    private const float REFRESH = 0.25f;

    private const string SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string SKIN_FILE = "skin.hor";

    private static readonly Vector4 Backdrop = new(0.04f, 0.05f, 0.08f, 0.82f);
    private static readonly Vector4 TextColour = new(0.93f, 0.95f, 1.0f, 1.0f);
    private static readonly Vector4 DimColour = new(0.62f, 0.66f, 0.74f, 1.0f);

    private UICompositor ui = null!;
    private UIModule module = null!;
    private StackPanel panel = null!;
    private Label headline = null!, details = null!;
    private LoopGraph frames = null!, ticks = null!;

    private float refresh;
    private long lastAllocated;
    private double lastRefreshAt;

    /// <summary>How much is shown, see <see cref="PerformanceDetail"/>. From any thread.</summary>
    public PerformanceDetail Detail { get; set; }

    /// <summary>The key that goes round the levels of detail, null for none.</summary>
    public Key? ToggleKey { get; set; } = Key.F3;

    /// <summary>Which corner of the screen it sits in.</summary>
    public Origin Corner { get; set; } = Origin.TopRight;

    /// <summary>How big it's drawn, a unit per pixel at 1.</summary>
    public float Scale { get; set; } = 1.0f;

    public PerformanceOverlay(PerformanceDetail detail = PerformanceDetail.Off)
    {
        Name = "Performance Overlay";
        Detail = detail;
    }

    public override void Initialize()
    {
        base.Initialize();

        // A UI of its own with a camera of its own, so it doesn't care what the game's cameras are up to
        ui = AddComponent(UICompositor.ForScreen(SKIN_DIRECTORY, SKIN_FILE));
        module = ui.CreateModule();
        module.ShowInLayoutDebugger = false;

        panel = module.AddComponent(new StackPanel
        {
            Padding = new UIEdges(12),
            Spacing = 6,
            Color = Backdrop,
            Radius = 6,
        });

        headline = panel.Add(new Label { Color = TextColour, Align = Origin.Left, Anchor = Origin.Left });
        details = panel.Add(new Label { Color = DimColour, Align = Origin.TopLeft, Anchor = Origin.Left });
        frames = panel.Add(new LoopGraph { Size = new Vector2(LoopStatistics.HISTORY, 44), Anchor = Origin.Left });
        ticks = panel.Add(new LoopGraph { Size = new Vector2(LoopStatistics.HISTORY, 28), Anchor = Origin.Left });
    }

    public override void UpdateState(float dt)
    {
        if (ToggleKey is { } key && Engine.Input.Keyboard.WasPressed(key))
            Detail = (PerformanceDetail)(((int)Detail + 1) % 3);

        module.Enabled = Detail != PerformanceDetail.Off;
        if (Detail != PerformanceDetail.Off)
        {
            ui.Scale = Scale;

            // Tucked into its corner, a bit off the edges
            Vector2 inset = Corner.ToVector() * -32.0f;
            panel.Anchor = Corner;
            panel.Position = inset;

            if ((refresh -= dt) <= 0.0f)
            {
                refresh = REFRESH;
                Write();
            }
        }

        // The UI of the overlay goes along with it, off or on
        base.UpdateState(dt);
    }

    /// <summary>
    /// Helper method to write out the numbers. Allocates a few strings four times a second, which is the price of reading
    /// numbers at all; the graphs reuse their buffers.
    /// </summary>
    private void Write()
    {
        var window = Engine.WindowManager;
        var loops = window.Loops;

        LoopStatistics? render = Find(loops, "Render"), simulation = Find(loops, "Simulation");
        LoopStatistics? logic = Find(loops, "Logic"), physics = Find(loops, "Physics");

        // The CPU's side of a frame and the GPU's, whichever is the bigger is what a slow frame is spent on
        double gpu = Engine.GpuFrameMs;
        headline.Text = render is null
            ? "starting up..."
            : gpu > 0.0 ? $"{render.Rate:0} fps   {render.WorkMs:0.0} ms cpu   {gpu:0.0} ms gpu" : $"{render.Rate:0} fps   {render.WorkMs:0.0} ms";

        bool full = Detail == PerformanceDetail.Full;
        details.Visible = frames.Visible = ticks.Visible = full;
        if (!full)
            return;

        // Garbage made since the last look, whoever made it (the loops only know their own)
        long allocated = GC.GetTotalAllocatedBytes(false);
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        double seconds = Math.Max(0.001, now - lastRefreshAt);
        double perSecond = lastRefreshAt > 0.0 ? (allocated - lastAllocated) / seconds : 0.0;
        lastAllocated = allocated;
        lastRefreshAt = now;

        var clock = window.Snapshots;
        var device = Engine.Graphics;
        var text = new System.Text.StringBuilder();

        // The threads, and what each is doing this very moment
        text.Append("threads     ");
        text.Append($"render {Doing(render)}");
        if (simulation is not null) text.Append($"   simulation {Doing(simulation)}");
        text.AppendLine();

        if (render is not null)
        {
            render.Instability(out double p99, out double worstPercent, out int stutters);
            text.AppendLine($"frames      {render.Rate:0}/s   {render.WorkMs:0.00} ms (worst {render.PeakWorkMs:0.0})   jitter {render.JitterMs:0.0} ms   gpu {gpu:0.00} ms");
            text.AppendLine($"unevenness  1% low {(worstPercent > 0.0 ? 1000.0 / worstPercent : 0.0):0} fps   p99 {p99:0.0} ms   stutters {stutters} of the last {LoopStatistics.HISTORY}");
        }
        if (simulation is not null)
            text.AppendLine($"simulation  {simulation.Rate:0}/{simulation.TargetRate:0} ticks   {simulation.WorkMs:0.00} ms   load {simulation.Load:0%}   waits {simulation.WaitMs:0.00} ms   dropped {simulation.DroppedTurns}");
        if (logic is not null)
            text.AppendLine($"   logic    {logic.Rate:0}/s   {logic.WorkMs:0.00} ms (worst {logic.PeakWorkMs:0.0})");
        if (physics is not null)
            text.AppendLine($"   physics  {physics.Rate:0}/s   {physics.WorkMs:0.00} ms (worst {physics.PeakWorkMs:0.0})");

        string presentation = window.Presentation == PresentationMode.Interpolated ? "interpolated" : "newest tick";
        text.AppendLine($"drawing     {presentation}, {clock.Delay * 1000.0:0.0} ms behind   tick every {clock.TickInterval * 1000.0:0.0} ms   #{window.Tick}");

        // What the frame cost the GPU, pass by pass, the ones that matter first
        var scopes = device.GpuScopes;
        if (scopes.Length > 0)
        {
            text.Append("gpu passes  ");
            int shown = 0;
            for (int i = 0; i < scopes.Length && shown < 7; i++)
            {
                if (scopes[i].Depth > 1 || scopes[i].Milliseconds < 0.005) continue;
                if (shown > 0) text.Append("   ");
                text.Append(scopes[i].Depth > 0 ? "  " : string.Empty).Append(scopes[i].Name).Append(' ').Append(scopes[i].Milliseconds.ToString("0.00"));
                shown++;
            }
            text.AppendLine();
        }

        var frame = device.Statistics.Last;
        text.AppendLine($"device      {frame.DrawCalls} draws ({frame.Instances} instances)   {frame.Dispatches} dispatches   {frame.PipelineBinds} pipelines   {frame.DescriptorSets} sets   {frame.Barriers} barriers   {(frame.BytesUploaded + frame.BytesStreamed) / 1024.0:0} KB up");
        text.AppendLine($"gpu memory  {device.Statistics.MemoryInUse / (1024.0 * 1024.0):0.0} MB in {device.Statistics.MemoryBlocks} blocks   {device.Statistics.Textures} textures   {device.Statistics.Buffers} buffers   {device.Statistics.Pipelines} pipelines");

        text.AppendLine($"garbage     {perSecond / 1024.0:0} KB/s   frames {render?.AllocatedPerSecond / 1024.0 ?? 0:0}  sim {simulation?.AllocatedPerSecond / 1024.0 ?? 0:0} KB/s");
        text.Append($"collections {GC.CollectionCount(0)} small  {GC.CollectionCount(1)} mid  {GC.CollectionCount(2)} big   heap {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB");
        details.Text = text.ToString();

        frames.Read(render);
        ticks.Read(simulation);
    }

    /// <summary>Helper method for what a loop's thread is up to, in a word.</summary>
    private static string Doing(LoopStatistics? loop) => loop?.State switch
    {
        LoopStatistics.LoopState.Working => "working",
        LoopStatistics.LoopState.Sleeping => "sleeping",
        LoopStatistics.LoopState.Blocked => "held up",
        LoopStatistics.LoopState.Stopped => "stopped",
        _ => "?"
    };

    private static LoopStatistics? Find(IReadOnlyList<LoopStatistics> loops, string name)
    {
        foreach (var loop in loops)
        {
            if (loop.Name == name)
                return loop;
        }

        return null;
    }

    /// <summary>
    /// How long the last few hundred turns of a loop took, as bars: green under its budget (a turn every 1/rate seconds,
    /// or 60 a second for frames with no limit), orange over it, red over twice it. The line is the budget.
    /// </summary>
    private sealed class LoopGraph : UIComponent
    {
        private readonly float[] history = new float[LoopStatistics.HISTORY];
        private float budget = 1000.0f / 60.0f;
        private string name = string.Empty;

        public void Read(LoopStatistics? loop)
        {
            if (loop is null)
                return;

            loop.CopyHistory(history);
            budget = 1000.0f / (float)(loop.TargetRate > 0.0 ? loop.TargetRate : 60.0);
            name = loop.Name;
        }

        protected override void Paint(UIDrawList list)
        {
            list.Rect(Bounds, new Vector4(0.0f, 0.0f, 0.0f, 0.35f));

            float top = budget * 2.5f;
            float width = Bounds.Width / history.Length;

            for (int i = 0; i < history.Length; i++)
            {
                float ms = history[i];
                if (ms <= 0.0f)
                    continue;

                float height = MathF.Min(1.0f, ms / top) * Bounds.Height;
                Vector4 colour = ms <= budget ? new(0.35f, 0.85f, 0.45f, 1.0f) : ms <= budget * 2.0f ? new(1.0f, 0.7f, 0.25f, 1.0f) : new(1.0f, 0.3f, 0.3f, 1.0f);

                float left = Bounds.Min.X + i * width;
                list.Rect(new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + MathF.Max(1.0f, width), Bounds.Min.Y + height)), colour);
            }

            float line = Bounds.Min.Y + budget / top * Bounds.Height;
            list.Rect(new UIRect(new Vector2(Bounds.Min.X, line), new Vector2(Bounds.Max.X, line + 1.0f)), new Vector4(1.0f, 1.0f, 1.0f, 0.5f));

            list.Text(name, Bounds.Shrink(new UIEdges(4.0f)), Origin.TopLeft, list.Skin.TextScale * 0.75f, new Vector4(1.0f, 1.0f, 1.0f, 0.7f), markup: false);
        }
    }
}
