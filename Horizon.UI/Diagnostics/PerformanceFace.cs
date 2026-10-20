using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Rendering;
using Horizon.UI.Components;

namespace Horizon.UI;

/// <summary>
/// What a <see cref="PerformanceOverlay"/> looks like, which is two layouts made in Hex like any other UI
/// (Assets/uix/diagnostics, performance_pill.hor for the minimal one and performance_card.hor for the lot) and
/// this, which finds their parts by name and writes what was last read off the engine
/// (<see cref="PerformanceSample"/>) into them. Where things are, how big and what colour is the layouts'
/// business, open them in Hex. What the numbers are and what colour trouble is, is this file's.
/// <para>
/// It used to be one component that painted all of it by hand, for the sake of making no garbage, a number in
/// a label is a string. It still makes none. Everything that changes is a <see cref="Readout"/>, which writes a
/// number into the characters it already has, the graphs are <see cref="Graph"/>s that are handed their bars as
/// numbers, and the few words that do have to be strings (what a number is counted in, out of how many ticks)
/// are only made anew when they change, which is next to never.
/// </para>
/// </summary>
internal sealed class PerformanceFace
{
    private const string DIRECTORY = "Assets/uix/diagnostics/";
    private const string PILL_FILE = "performance_pill.hor", CARD_FILE = "performance_card.hor";

    // How many times what is usual the top of a graph is. One frame of three hundred milliseconds while a scene
    // loads and twelve seconds of graph are a flat line with a pole in it if the top is the worst there is
    private const float HEADROOM = 2.5f;

    // How far in a pass that is a part of the one above it sits, the chip and the name
    private const float PASS_CHIP = 1.0f, PASS_NAME = 15.0f, PASS_INDENT = 13.0f;

    // How far the gap between two sections is, and so between the two columns when they are side by side
    private const float COLUMN_GAP = 6.0f;

    /* The colours of how things are going. What things are painted in when they are fine is the layouts' */

    private static readonly Vector4 Bright = new(0.96f, 0.97f, 1.0f, 1.0f);
    private static readonly Vector4 Plain = new(0.80f, 0.83f, 0.90f, 1.0f);
    private static readonly Vector4 Dim = new(0.62f, 0.66f, 0.75f, 1.0f);
    private static readonly Vector4 Accent = new(0.38f, 0.62f, 1.0f, 1.0f);
    private static readonly Vector4 Good = new(0.36f, 0.86f, 0.55f, 1.0f);
    private static readonly Vector4 Warn = new(1.0f, 0.74f, 0.30f, 1.0f);
    private static readonly Vector4 Bad = new(1.0f, 0.40f, 0.42f, 1.0f);

    /// <summary>The parts of a line of a thread.</summary>
    private readonly record struct ThreadRow(Panel Row, Panel Dot, Readout State, Readout Rate, Readout Milliseconds, Meter Meter, Readout Load);

    /// <summary>The parts of a line of something a thread does every turn.</summary>
    private readonly record struct PartRow(Panel Row, Readout Rate, Readout Milliseconds, Readout Worst);

    /// <summary>The parts of a line of a pass of the GPU.</summary>
    private readonly record struct PassRow(Panel Row, Panel Chip, Readout Name, Readout Milliseconds, Readout Share);

    private readonly PerformanceSample sample;

    /* The pill */

    private readonly StackPanel pill, pillNumbers, pillGpu;
    private readonly Panel pillDot, pillCap;
    private readonly Label pillStarting, pillKey;
    private readonly Readout pillFps, pillCpu, pillGpuMs;
    private readonly Graph spark;

    /* The card */

    private readonly StackPanel card;
    private readonly Divider middle;
    private readonly Label starting, cardKey;
    private readonly Panel cap, cpuTile, gpuTile;
    private readonly Readout fps, frameMs, cpuLoad, cpuMs, gpuLoad, gpuMs;
    private readonly Meter cpuMeter, gpuMeter;
    private readonly Graph frameGraph, tickGraph;
    private readonly Readout low, p99, worst, jitter, stutters;
    private readonly Readout threadsNote, waits, late, dropped, tick;
    private readonly ThreadRow render, simulation;
    private readonly PartRow logic, physics;
    private readonly StackPanel ticks;
    private readonly Readout gpuNote;
    private readonly SegmentBar gpuBar;
    private readonly Label gpuSilent;
    private readonly PassRow[] passes;
    private readonly Readout draws, instances, dispatches, barriers, pipelineBinds, sets, targets, sent;
    private readonly Readout memoryGpu, memoryGpuLabel, textures, buffers, pipelines, heap, garbage, byFrames, byTicks;
    private readonly Readout small, mid, big;

    // The most lines the passes of the GPU have had while this has been up. A card that grows and shrinks by a
    // line whenever a pass comes or goes hops about in its corner
    private int rowsHeld;

    // What the rate of a thread was last said to be out of, the words for it are only made when that changes
    private double renderOutOf = -1.0, simulationOutOf = -1.0;

    /// <summary>The minimal one and the lot, whichever is shown is the one that is visible.</summary>
    public UIComponent Pill => pill;

    /// <inheritdoc cref="Pill"/>
    public UIComponent Card => card;

    /// <summary>How tall the card may be, in its own units. One that would be taller has its two columns side by side.</summary>
    public float MaxHeight { get; set; } = float.MaxValue;

    public PerformanceFace(UIModule module, PerformanceSample sample)
    {
        this.sample = sample;

        UILayout small_ = module.LoadLayout(DIRECTORY + PILL_FILE);
        pill = small_.Get<StackPanel>("pill");
        pillNumbers = small_.Get<StackPanel>("numbers");
        pillGpu = small_.Get<StackPanel>("gpu");
        pillDot = small_.Get<Panel>("dot");
        pillCap = small_.Get<Panel>("cap");
        pillStarting = small_.Get<Label>("starting");
        pillKey = small_.Get<Label>("cap_key");
        pillFps = small_.Get<Readout>("fps");
        pillCpu = small_.Get<Readout>("cpu_ms");
        pillGpuMs = small_.Get<Readout>("gpu_ms");
        spark = small_.Get<Graph>("spark");

        UILayout big_ = module.LoadLayout(DIRECTORY + CARD_FILE);
        Readout Number(string name) => big_.Get<Readout>(name);

        card = big_.Get<StackPanel>("card");
        middle = big_.Get<Divider>("middle");
        starting = big_.Get<Label>("starting");
        cap = big_.Get<Panel>("cap");
        cardKey = big_.Get<Label>("cap_key");
        cpuTile = big_.Get<Panel>("cpu_tile");
        gpuTile = big_.Get<Panel>("gpu_tile");
        fps = Number("fps");
        frameMs = Number("frame_ms");
        cpuLoad = Number("cpu_load");
        cpuMs = Number("cpu_ms");
        gpuLoad = Number("gpu_load");
        gpuMs = Number("gpu_ms");
        cpuMeter = big_.Get<Meter>("cpu_meter");
        gpuMeter = big_.Get<Meter>("gpu_meter");

        frameGraph = big_.Get<Graph>("frame_graph");
        low = Number("low");
        p99 = Number("p99");
        worst = Number("worst");
        jitter = Number("jitter");
        stutters = Number("stutters");

        threadsNote = Number("threads_note");
        render = Thread(big_, "render");
        simulation = Thread(big_, "simulation");
        logic = Part(big_, "logic");
        physics = Part(big_, "physics");
        ticks = big_.Get<StackPanel>("ticks");
        tickGraph = big_.Get<Graph>("tick_graph");
        waits = Number("waits");
        late = Number("late");
        dropped = Number("dropped");
        tick = Number("tick");

        gpuNote = Number("gpu_note");
        gpuBar = big_.Get<SegmentBar>("gpu_bar");
        gpuSilent = big_.Get<Label>("gpu_silent");

        // A line for every pass there can be, made once. The ones nobody has a pass for are not shown
        var lines = big_.Populate(big_.Get<StackPanel>("gpu_rows"), PerformanceSample.MAX_ROWS);
        passes = new PassRow[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            passes[i] = new PassRow(
                lines[i].Get<Panel>("pass"), lines[i].Get<Panel>("chip"), lines[i].Get<Readout>("name"),
                lines[i].Get<Readout>("ms"), lines[i].Get<Readout>("share"));
        }

        draws = Number("device_draws");
        instances = Number("device_instances");
        dispatches = Number("device_dispatches");
        barriers = Number("device_barriers");
        pipelineBinds = Number("device_pipelines");
        sets = Number("device_sets");
        targets = Number("device_targets");
        sent = Number("device_sent");

        memoryGpu = Number("memory_gpu");
        memoryGpuLabel = Number("memory_gpu_label");
        textures = Number("memory_textures");
        buffers = Number("memory_buffers");
        pipelines = Number("memory_pipelines");
        heap = Number("memory_heap");
        garbage = Number("memory_garbage");
        byFrames = Number("memory_frames");
        byTicks = Number("memory_ticks");
        small = Number("small");
        mid = Number("mid");
        big = Number("big");
    }

    private static ThreadRow Thread(UILayout layout, string name) => new(
        layout.Get<Panel>($"{name}_row"), layout.Get<Panel>($"{name}_dot"), layout.Get<Readout>($"{name}_state"),
        layout.Get<Readout>($"{name}_rate"), layout.Get<Readout>($"{name}_ms"), layout.Get<Meter>($"{name}_meter"),
        layout.Get<Readout>($"{name}_load"));

    private static PartRow Part(UILayout layout, string name) => new(
        layout.Get<Panel>($"{name}_row"), layout.Get<Readout>($"{name}_rate"), layout.Get<Readout>($"{name}_ms"),
        layout.Get<Readout>($"{name}_worst"));

    /// <summary>Forgets what it remembered of the last time it was up, for when the overlay changes what it shows.</summary>
    public void Reset() => rowsHeld = 0;

    /// <summary>Writes the name of the key that gets more on its caps, nothing for no cap at all.</summary>
    public void SetKey(string key)
    {
        pillCap.Visible = cap.Visible = key.Length > 0;
        pillKey.Text = cardKey.Text = key;
    }

    /// <summary>
    /// Writes what was last read off the engine into whichever of the two is up. Four times a second, see
    /// <see cref="ShowGraphs"/> for what is done more often.
    /// </summary>
    public void Show(PerformanceDetail detail)
    {
        if (detail == PerformanceDetail.Full) ShowCard();
        else ShowPill();

        ShowGraphs(detail);
    }

    /// <summary>Moves the graphs on, which is all that changes between two readings of the numbers.</summary>
    public void ShowGraphs(PerformanceDetail detail)
    {
        float frameTop = Graph.RoundUp(MathF.Max(sample.FrameUsualMs * HEADROOM, 0.05f));
        float budget = (float)sample.FrameBudgetMs;

        if (detail != PerformanceDetail.Full)
        {
            Feed(spark, sample.Frames, sample.FrameSlices, gaps: true, frameTop, 0.0f, budget);
            return;
        }

        // The line is where the frames are meant to stay under, there is none for frames nobody set a limit on
        Feed(frameGraph, sample.Frames, sample.FrameSlices, gaps: true, frameTop, sample.FpsTarget > 0.0 ? budget : 0.0f, budget);

        if (sample.HasSimulation)
        {
            float tickBudget = (float)sample.TickBudgetMs;
            Feed(tickGraph, sample.Ticks, sample.TickSlices, gaps: false, Graph.RoundUp(MathF.Max(sample.TickUsualMs * HEADROOM, 0.05f)), tickBudget, tickBudget);
        }
    }

    /// <summary>
    /// Helper method to hand a graph the timeline of a loop, a bar for every tenth of a second.
    /// </summary>
    /// <param name="gaps">Whether it is how far apart the turns came that is drawn (the frames, as the eye gets them) or how long they worked for (the ticks, against their budget).</param>
    private static void Feed(Graph graph, LoopSlice[] slices, int filled, bool gaps, float top, float line, float budget)
    {
        if (graph.Bars != slices.Length) graph.Bars = slices.Length;

        graph.Count = filled;
        graph.Top = top;
        graph.Line = line;
        graph.Budget = budget;

        for (int i = slices.Length - filled; i < slices.Length; i++)
        {
            if (gaps) graph.Set(i, slices[i].GapMs, slices[i].WorstGapMs);
            else graph.Set(i, slices[i].WorkMs, slices[i].WorstWorkMs);
        }
    }

    /* The pill */

    private void ShowPill()
    {
        pillDot.Color = sample.Ready ? Health() : Dim;
        pillStarting.Visible = !sample.Ready;
        pillNumbers.Visible = sample.Ready;
        if (!sample.Ready)
            return;

        pillFps.Set(Math.Min(sample.Fps, 9999.0), "0");
        pillCpu.Set(Math.Min(sample.WorkMs, 999.0), sample.WorkMs < 99.95 ? "0.0" : "0");

        // A device that doesn't time its frames has nothing to put here
        pillGpu.Visible = sample.GpuMs > 0.0;
        if (pillGpu.Visible)
            pillGpuMs.Set(Math.Min(sample.GpuMs, 999.0), sample.GpuMs < 99.95 ? "0.0" : "0");
    }

    /* The card */

    private void ShowCard()
    {
        Arrange();

        starting.Visible = !sample.Ready;
        fps.Visible = frameMs.Visible = cpuTile.Visible = sample.Ready;
        gpuTile.Visible = sample.Ready && sample.GpuMs > 0.0;

        if (sample.Ready)
            ShowHeader();

        ShowFrames();
        ShowThreads();
        ShowGpu();
        ShowDevice();
        ShowMemory();
    }

    /// <summary>
    /// Helper method to put the two columns of the card one under the other if the screen is tall enough for
    /// that, the way these things are worn nowadays, and side by side if it isn't.
    /// </summary>
    private void Arrange()
    {
        if (card.Children.Count < 3)
            return;

        // Counted through by number. Going through them with a foreach makes something to go through them with
        // every time, in the one place that is there to count the garbage
        float tall = card.Padding.Total.Y;
        var columns = card.Children;
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i] is StackPanel) tall += columns[i].DesiredSize.Y + COLUMN_GAP;
        }

        bool wide = tall + COLUMN_GAP > MaxHeight;
        UIDirection wanted = wide ? UIDirection.Horizontal : UIDirection.Vertical;
        if (card.Direction == wanted && middle.Direction != wanted)
            return;

        // The rule between them runs the other way to the way they are stacked
        card.Direction = wanted;
        middle.Direction = wide ? UIDirection.Vertical : UIDirection.Horizontal;
        middle.Fill = wide ? UIFill.Vertical : UIFill.Horizontal;
    }

    private void ShowHeader()
    {
        // White while the frames do what they are meant to, the colour of the trouble they are in when they don't
        Vector4 health = Health();
        fps.Set(Math.Min(sample.Fps, 99999.0), "0");
        fps.Color = health == Good ? Bright : health;

        frameMs.Clear().Add(sample.FrameMs, sample.FrameMs < 99.95 ? "0.00" : "0.0").Add(" ms a frame");

        // Never more than all of a frame, whatever two numbers that are each smoothed their own way come to
        Tile(cpuLoad, cpuMs, cpuMeter, sample.WorkMs, Math.Min(sample.RenderLoad, 1.0));
        if (sample.GpuMs > 0.0)
            Tile(gpuLoad, gpuMs, gpuMeter, sample.GpuMs, Math.Min(sample.GpuLoad, 1.0));
    }

    /// <summary>Helper method to write what a frame costs one side of the machine, and how much of the frame that is.</summary>
    private static void Tile(Readout share, Readout cost, Meter meter, double milliseconds, double load)
    {
        share.Clear().Add(Math.Clamp(load, 0.0, 9.99) * 100.0, "0").Add("%");
        cost.Set(milliseconds, milliseconds < 99.95 ? "0.00" : "0.0");
        meter.Value = (float)load;
    }

    private void ShowFrames()
    {
        double budget = sample.FrameBudgetMs;

        low.Set(sample.Low1Fps, "0");
        p99.Set(sample.P99Ms, "0.0");
        worst.Set(sample.WorstGapMs, "0.0");
        worst.Color = Against(sample.WorstGapMs, budget, Bright);
        jitter.Set(sample.JitterMs, "0.00");
        stutters.Set(sample.Stutters);
        stutters.Color = sample.Stutters > 0 ? Warn : Bright;
    }

    private void ShowThreads()
    {
        // How far in the past the frames show, which is what drawing between two ticks costs
        threadsNote.Visible = sample.HasSimulation;
        if (sample.HasSimulation)
            threadsNote.Clear().Add(sample.Interpolated ? "interpolated, " : "newest tick, ").Add(sample.BehindMs, "0.0").Add(" ms behind");

        ShowThread(render, sample.RenderState, sample.Fps, sample.FpsTarget, ref renderOutOf, sample.WorkMs, Math.Min(sample.RenderLoad, 1.0), Bright, Accent);

        simulation.Row.Visible = sample.HasSimulation;
        if (sample.HasSimulation)
        {
            // Not keeping up is the one thing in here that is always bad news
            bool behind = sample.TickTarget > 0.0 && sample.TickRate < sample.TickTarget * 0.97;
            Vector4 rate = sample.Dropping ? Bad : behind ? Warn : Bright;
            Vector4 bar = sample.TickLoad >= 1.0 ? Bad : sample.TickLoad >= 0.8 ? Warn : Accent;

            ShowThread(simulation, sample.SimulationState, sample.TickRate, sample.TickTarget, ref simulationOutOf, sample.TickMs, sample.TickLoad, rate, bar);
        }

        logic.Row.Visible = sample.HasLogic;
        if (sample.HasLogic)
            ShowPart(logic, sample.LogicRate, sample.LogicMs, sample.LogicPeakMs);

        physics.Row.Visible = sample.HasPhysics;
        if (sample.HasPhysics)
            ShowPart(physics, sample.PhysicsRate, sample.PhysicsMs, sample.PhysicsPeakMs);

        ticks.Visible = sample.HasSimulation;
        if (!sample.HasSimulation)
            return;

        waits.Set(sample.TickWaitMs, "0.00");
        late.Set(sample.Late);
        dropped.Set(sample.Dropped);
        dropped.Color = sample.Dropping ? Bad : sample.Dropped > 0 ? Warn : Plain;
        tick.Clear().Add("tick ").Add(sample.Tick);
    }

    /// <summary>
    /// Helper method to write the line of a thread, what it is doing right now, how often it comes round, what a
    /// turn costs it and how much of its time that is.
    /// </summary>
    /// <param name="outOf">What its rate was last said to be out of, so the words for that are only made when it changes.</param>
    private static void ShowThread(in ThreadRow row, LoopStatistics.LoopState state, double rate, double target, ref double outOf, double milliseconds, double load, Vector4 rateColour, Vector4 barColour)
    {
        row.Dot.Color = state switch
        {
            LoopStatistics.LoopState.Working => Good,
            LoopStatistics.LoopState.Sleeping => Accent,
            LoopStatistics.LoopState.Blocked => Warn,
            _ => Dim
        };

        row.State.Write(Doing(state));

        // Out of how many it is meant to be, for one that is meant to be anything. A string, so only when it changes
        if (target != outOf)
        {
            outOf = target;
            row.Rate.Unit = target > 0.0 ? $"/{target:0}" : "/s";
        }

        row.Rate.Set(rate, "0");
        row.Rate.Color = rateColour;
        row.Milliseconds.Set(milliseconds, "0.00");
        row.Meter.Value = (float)load;
        row.Meter.Color = barColour;
        row.Load.Clear().Add(Math.Clamp(load, 0.0, 9.99) * 100.0, "0").Add("%");
    }

    /// <summary>Helper method to write the line of something a thread does every turn (the logic, the physics).</summary>
    private static void ShowPart(in PartRow row, double rate, double milliseconds, double peak)
    {
        row.Rate.Set(rate, "0");
        row.Milliseconds.Set(milliseconds, "0.00");
        row.Worst.Clear().Add("worst ").Add(peak, "0.0");
    }

    /// <summary>What a thread is up to, in a word. Sleeping is waiting for its next turn to be due, which is a thread with time to spare.</summary>
    private static string Doing(LoopStatistics.LoopState state) => state switch
    {
        LoopStatistics.LoopState.Working => "working",
        LoopStatistics.LoopState.Sleeping => "sleeping",
        LoopStatistics.LoopState.Blocked => "held up",
        _ => "stopped"
    };

    /// <summary>
    /// Helper method to write what the frame cost the GPU, pass by pass. One bar cut up by how much of the frame
    /// each of them took and a line for each under it in the same colour, with what the dearer ones are made of.
    /// A pass that is made of others is those on the bar and, in its own colour, whatever of it they don't
    /// account for. One renderer that is 99 parts of the frame in a hundred makes for a bar that says fuck all
    /// otherwise. The same name is one line however often the frame did it.
    /// </summary>
    private void ShowGpu()
    {
        rowsHeld = Math.Min(passes.Length, Math.Max(rowsHeld, Math.Max(1, sample.RowCount)));
        double whole = Math.Max(sample.GpuMs, sample.PassTotal);

        gpuNote.Clear();
        if (whole > 0.0) gpuNote.Add(whole, "0.00").Add(" ms a frame");

        gpuBar.Clear();
        gpuBar.Whole = (float)whole;
        if (whole > 0.0)
        {
            for (int row = 0; row < sample.RowCount; row++)
            {
                int index = sample.Rows[row];
                ref readonly var pass = ref sample.Passes[index];

                // What it is made of comes straight after it in the lines, and before what is left of it on the bar
                double pieces = 0.0;
                while (row + 1 < sample.RowCount && sample.Passes[sample.Rows[row + 1]].Parent == index)
                {
                    ref readonly var piece = ref sample.Passes[sample.Rows[++row]];
                    gpuBar.Add((float)piece.Milliseconds, SegmentBar.Shade(piece.Colour), piece.Colour);
                    pieces += piece.Milliseconds;
                }

                gpuBar.Add((float)Math.Max(0.0, pass.Milliseconds - pieces), SegmentBar.Shade(pass.Colour), pass.Colour);
            }
        }

        gpuSilent.Visible = sample.RowCount == 0;

        for (int row = 0; row < passes.Length; row++)
        {
            ref readonly PassRow line = ref passes[row];

            // A line that is held but has no pass on it keeps its room and shows nothing
            line.Row.Visible = row < rowsHeld && sample.RowCount > 0;
            bool has = row < sample.RowCount;
            line.Chip.Visible = line.Name.Visible = line.Milliseconds.Visible = line.Share.Visible = has;
            if (!has)
                continue;

            ref readonly var pass = ref sample.Passes[sample.Rows[row]];
            bool own = pass.Parent < 0;

            // A chip of its colour on the bar, a bit in for what is a part of the pass above it
            float indent = own ? 0.0f : PASS_INDENT;
            line.Chip.Position = line.Chip.Position with { X = PASS_CHIP + indent };
            line.Chip.Color = SegmentBar.Shade(pass.Colour);
            line.Name.Position = line.Name.Position with { X = PASS_NAME + indent };

            line.Name.Clear().Add(pass.Name);
            if (pass.Count > 1) line.Name.Add("  x").Add(pass.Count);
            line.Name.Color = own ? Plain : Dim;

            line.Milliseconds.Set(pass.Milliseconds, "0.00");
            line.Milliseconds.Color = own ? Bright : Plain;

            line.Share.Clear();
            if (whole > 0.0) line.Share.Add(pass.Milliseconds / whole * 100.0, "0").Add("%");
            line.Share.Color = own ? Plain : Dim;
        }
    }

    /// <summary>Helper method to write what the last frame asked of the device, the things that add up when there are too many of them.</summary>
    private void ShowDevice()
    {
        var frame = sample.Frame;

        draws.Set(frame.DrawCalls);
        instances.Set(frame.Instances);
        dispatches.Set(frame.Dispatches);
        barriers.Set(frame.Barriers);
        pipelineBinds.Set(frame.PipelineBinds);
        sets.Set(frame.DescriptorSets);
        targets.Set(frame.RenderTargetBinds);

        double up = frame.BytesUploaded + frame.BytesStreamed;
        sent.Set(Amount(up, out string unit), up < 1024.0 * 1024.0 ? "0" : "0.0");
        sent.Unit = unit;
    }

    /// <summary>
    /// Helper method to write what the GPU is holding and what the garbage collector has to deal with. Garbage that
    /// never stops climbing is what ends up as a hitch when the collector cleans up after it, so who is making it
    /// has a number each.
    /// </summary>
    private void ShowMemory()
    {
        memoryGpu.Set(Amount(sample.GpuMemory, out string unit), "0.0");
        memoryGpu.Unit = unit;
        memoryGpuLabel.Clear().Add("gpu, ").Add(sample.Blocks).Add(sample.Blocks == 1 ? " block" : " blocks");

        textures.Set(sample.Textures);
        buffers.Set(sample.Buffers);
        pipelines.Set(sample.Pipelines);

        heap.Set(Amount(sample.HeapBytes, out unit), "0.0");
        heap.Unit = unit;
        garbage.Set(Rate(sample.GarbagePerSecond, out unit), "0");
        garbage.Unit = unit;
        byFrames.Set(Rate(sample.RenderGarbage, out unit), "0");
        byFrames.Unit = unit;
        byTicks.Set(Rate(sample.SimulationGarbage, out unit), "0");
        byTicks.Unit = unit;

        small.Set(sample.Small);
        mid.Set(sample.Mid);
        big.Set(sample.Big);
    }

    /// <summary>The colour of how the frames are doing against what they are meant to do, sixty a second if nobody said.</summary>
    private Vector4 Health()
    {
        double wanted = sample.FpsTarget > 0.0 ? sample.FpsTarget : 60.0;
        return sample.Fps >= wanted * 0.95 ? Good : sample.Fps >= wanted * 0.5 ? Warn : Bad;
    }

    /// <summary>The colour of something that took so long against what it may take.</summary>
    private static Vector4 Against(double milliseconds, double budget, Vector4 fine) =>
        budget <= 0.0 || milliseconds <= budget ? fine : milliseconds <= budget * 2.0 ? Warn : Bad;

    /// <summary>Helper method to put a number of bytes in whatever it is best counted in.</summary>
    private static double Amount(double bytes, out string unit)
    {
        const double KB = 1024.0, MB = KB * 1024.0, GB = MB * 1024.0;

        if (bytes < MB)
        {
            unit = "KB";
            return bytes / KB;
        }

        if (bytes < GB)
        {
            unit = "MB";
            return bytes / MB;
        }

        unit = "GB";
        return bytes / GB;
    }

    /// <summary>Helper method to put a number of bytes a second in whatever it is best counted in.</summary>
    private static double Rate(double bytes, out string unit)
    {
        if (bytes < 1024.0 * 1024.0 * 10.0)
        {
            unit = "KB/s";
            return bytes / 1024.0;
        }

        unit = "MB/s";
        return bytes / (1024.0 * 1024.0);
    }
}
