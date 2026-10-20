using System.Collections.Concurrent;
using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Logging;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Engine.Debugging;

/// <summary>
/// The numbers of the frame, the same ones the performance overlay reads (F3 has the graphs), laid out to be
/// read rather than glanced at. What the loops are doing and the device was asked for on the left, and on the
/// right every pass of the frame the renderers name with what it cost the GPU, as a bar against the whole frame.
/// With the game in its container it is still drawn at the size of the window, so these are the numbers of the
/// game as it ships plus the one copy and the panels, which show up as the pass called ui.
/// </summary>
internal sealed class MetricsView : UIComponent
{
    private const float ROW_HEIGHT = 18.0f;
    private const float REFRESH = 0.25f;
    private const float LEFT_SHARE = 0.5f;
    private const float PASS_NAME = 190.0f;
    private const float PASS_TIME = 62.0f;

    private readonly List<(string Name, string Value)> lines = [];
    private readonly List<(string Name, double Milliseconds, int Depth)> passes = [];

    private double frameGpu;
    private float timer;
    private long lastAllocated;
    private double lastAt;

    protected override Vector2 Measure(UISkin skin) => new(0.0f, (Math.Max(lines.Count, passes.Count + 1) + 1) * ROW_HEIGHT);

    protected override void Update(float dt)
    {
        if ((timer -= dt) > 0.0f)
            return;

        timer = REFRESH;
        Read();
    }

    private static LoopStatistics? Find(IReadOnlyList<LoopStatistics> loops, string name)
    {
        foreach (var loop in loops)
        {
            if (loop.Name == name) return loop;
        }

        return null;
    }

    private void Read()
    {
        var engine = GameObject.Engine;
        var window = engine.WindowManager;
        var device = engine.Graphics;

        LoopStatistics? render = Find(window.Loops, "Render"), simulation = Find(window.Loops, "Simulation");
        LoopStatistics? logic = Find(window.Loops, "Logic"), physics = Find(window.Loops, "Physics");

        long allocated = GC.GetTotalAllocatedBytes(false);
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        double garbage = lastAt > 0.0 ? (allocated - lastAllocated) / Math.Max(0.001, now - lastAt) : 0.0;
        (lastAllocated, lastAt) = (allocated, now);

        frameGpu = engine.GpuFrameMs;

        lines.Clear();
        if (render is not null)
        {
            render.Instability(out double p99, out double worst, out int stutters);
            lines.Add(("frames", $"{render.Rate:0} a second, {render.WorkMs:0.00} ms on the CPU, {frameGpu:0.00} ms on the GPU"));
            lines.Add(("unevenness", $"1% low {(worst > 0.0 ? 1000.0 / worst : 0.0):0} fps, p99 {p99:0.0} ms, {stutters} stutters in {LoopStatistics.HISTORY}"));
        }
        if (simulation is not null)
            lines.Add(("simulation", $"{simulation.Rate:0} of {simulation.TargetRate:0} ticks, {simulation.WorkMs:0.00} ms, load {simulation.Load:0%}, dropped {simulation.DroppedTurns}"));
        if (logic is not null)
            lines.Add(("logic", $"{logic.WorkMs:0.00} ms a tick, worst {logic.PeakWorkMs:0.0}"));
        if (physics is not null)
            lines.Add(("physics", $"{physics.WorkMs:0.00} ms a tick, worst {physics.PeakWorkMs:0.0}"));

        var scenes = engine.SceneManager;
        lines.Add(("scene", scenes.Paused ? "paused" : scenes.TimeScale == 1.0f ? "running" : $"running at {scenes.TimeScale:0.##} of its speed"));

        var frame = device.Statistics.Last;
        lines.Add(("draws", $"{frame.DrawCalls} with {frame.Instances} instances, {frame.Dispatches} dispatches"));
        lines.Add(("binds", $"{frame.PipelineBinds} pipelines, {frame.DescriptorSets} sets, {frame.RenderTargetBinds} targets, {frame.Barriers} barriers"));
        lines.Add(("sent up", $"{(frame.BytesUploaded + frame.BytesStreamed) / 1024.0:0} KB this frame"));
        lines.Add(("GPU memory", $"{device.Statistics.MemoryInUse / (1024.0 * 1024.0):0.0} MB in {device.Statistics.MemoryBlocks} blocks"));
        lines.Add(("made", $"{device.Statistics.Textures} textures, {device.Statistics.Buffers} buffers, {device.Statistics.Pipelines} pipelines"));
        lines.Add(("garbage", $"{garbage / 1024.0:0} KB a second, heap {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.0} MB"));
        lines.Add(("collections", $"{GC.CollectionCount(0)} small, {GC.CollectionCount(1)} mid, {GC.CollectionCount(2)} big"));

        passes.Clear();
        foreach (var scope in device.GpuScopes)
            passes.Add((scope.Name, scope.Milliseconds, scope.Depth));
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = skin.TextScale;
        UIRect area = Bounds.Shrink(new UIEdges(10.0f, 6.0f));
        float split = area.Min.X + area.Width * LEFT_SHARE;

        float top = area.Max.Y;
        foreach (var (name, value) in lines)
        {
            list.Text(name, new UIRect(new Vector2(area.Min.X, top - ROW_HEIGHT), new Vector2(area.Min.X + 96.0f, top)), Origin.Left, scale, DebugStyle.Dim, markup: false);
            list.Text(value, new UIRect(new Vector2(area.Min.X + 96.0f, top - ROW_HEIGHT), new Vector2(split - 8.0f, top)), Origin.Left, scale, skin.TextColor, markup: false);
            top -= ROW_HEIGHT;
        }

        // The passes, every one as a bar against what the whole frame cost
        float left = split + 8.0f;
        top = area.Max.Y;
        list.Text(passes.Count > 0 ? "what the frame cost the GPU, pass by pass" : "the GPU has not said what its passes cost", new UIRect(new Vector2(left, top - ROW_HEIGHT), new Vector2(area.Max.X, top)), Origin.Left, scale, DebugStyle.Dim, markup: false);
        top -= ROW_HEIGHT;

        double whole = Math.Max(frameGpu, 0.001);
        float barLeft = left + PASS_NAME + PASS_TIME;
        float barRoom = MathF.Max(0.0f, area.Max.X - barLeft);

        foreach (var (name, milliseconds, depth) in passes)
        {
            var row = new UIRect(new Vector2(left, top - ROW_HEIGHT), new Vector2(area.Max.X, top));
            float indent = depth * 12.0f;

            list.Text(DebugStyle.Fit(skin, name, PASS_NAME - indent - 6.0f), new UIRect(new Vector2(left + indent, row.Min.Y), row.Max), Origin.Left, scale, depth == 0 ? skin.TextColor : DebugStyle.Dim, markup: false);
            list.Text($"{milliseconds:0.000}", new UIRect(new Vector2(left + PASS_NAME, row.Min.Y), new Vector2(barLeft - 8.0f, row.Max.Y)), Origin.Right, scale, skin.TextColor, markup: false);

            float share = (float)Math.Clamp(milliseconds / whole, 0.0, 1.0);
            Vector4 colour = share < 0.25f ? DebugStyle.Good : share < 0.5f ? DebugStyle.Warn : DebugStyle.Bad;
            list.Rect(new UIRect(new Vector2(barLeft, row.Min.Y + 5.0f), new Vector2(barLeft + barRoom, row.Max.Y - 5.0f)), new Vector4(0.0f, 0.0f, 0.0f, 0.3f));
            list.Rect(new UIRect(new Vector2(barLeft, row.Min.Y + 5.0f), new Vector2(barLeft + barRoom * share, row.Max.Y - 5.0f)), colour with { W = depth == 0 ? 1.0f : 0.6f });

            top -= ROW_HEIGHT;
        }
    }
}

/// <summary>
/// The log as it is written, the last few hundred lines of it, in the colours the console has them in. It stays
/// at the newest line for as long as it is left scrolled all the way down.
/// </summary>
internal sealed class LogView : UIComponent
{
    private const float ROW_HEIGHT = 17.0f;
    private const int MOST_LINES = 400;

    private readonly ConcurrentQueue<(LogLevel Level, string Text)> heard = new();
    private readonly List<(LogLevel Level, string Text)> lines = [];

    /// <summary>Takes a line of the log, from whatever thread wrote it.</summary>
    public void Hear(LogLevel level, string message) => heard.Enqueue((level, message));

    protected override Vector2 Measure(UISkin skin) => new(0.0f, lines.Count * ROW_HEIGHT + 8.0f);

    protected override void Update(float dt)
    {
        if (heard.IsEmpty)
            return;

        var scroll = Parent as ScrollPanel;
        bool atEnd = scroll is null || scroll.Offset >= scroll.MaxOffset - 1.0f;

        while (heard.TryDequeue(out var line))
        {
            // A message of several lines is that many rows
            foreach (string part in line.Text.Split('\n'))
                lines.Add((line.Level, part.TrimEnd('\r')));
        }

        if (lines.Count > MOST_LINES)
            lines.RemoveRange(0, lines.Count - MOST_LINES);

        if (atEnd)
            scroll?.ScrollToEnd();
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect view = Parent?.Bounds ?? Bounds;
        float topEdge = Bounds.Max.Y - 4.0f;

        int first = Math.Max(0, (int)((topEdge - view.Max.Y) / ROW_HEIGHT));
        int last = Math.Min(lines.Count - 1, (int)((topEdge - view.Min.Y) / ROW_HEIGHT));

        for (int i = first; i <= last; i++)
        {
            var (level, text) = lines[i];
            float top = topEdge - i * ROW_HEIGHT;
            Vector4 colour = level switch
            {
                LogLevel.Success => DebugStyle.Good,
                LogLevel.Warning => DebugStyle.Warn,
                LogLevel.Error or LogLevel.Fatal => DebugStyle.Bad,
                _ => skin.TextColor
            };

            list.Text(text, new UIRect(new Vector2(Bounds.Min.X + 8.0f, top - ROW_HEIGHT), new Vector2(Bounds.Max.X, top)), Origin.Left, skin.TextScale * 0.95f, colour, markup: false);
        }
    }
}
