using System.Diagnostics;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.UI;

/// <summary>
/// What a <see cref="PerformanceOverlay"/> last read off the engine, kept in one place so the board that draws it
/// never has to ask a loop or the device anything itself. Read on the simulation thread a few times a second, into
/// the same fields and the same arrays every time, so reading the numbers makes no garbage of its own. An overlay
/// that shows how much garbage there is and makes half of it would be a bit of a prick.
/// </summary>
internal sealed class PerformanceSample
{
    /// <summary>A pass of the frame on the GPU, every stretch of one name under the same parent added up.</summary>
    public struct Pass
    {
        public string Name;
        public double Milliseconds;

        // How many stretches went into it, the four particle renderers of a fight are one line and not four
        public int Count;

        // Which pass it is a part of, -1 for one of the frame's own
        public int Parent;

        // Which of the board's colours it gets, the line it has. The lines are in the order of the frame, so a
        // pass keeps its colour from read to read for as long as the frame does the same things
        public int Colour;

        public bool Shown;
    }

    /// <summary>How many passes are kept, how many of them get a line, and how many of those can be the frame's own.</summary>
    public const int MAX_PASSES = 48, MAX_ROWS = 8, MAX_TOP = 6;

    // A pass that cost less than this (in milliseconds) is not worth a line
    private const double SMALL = 0.005;

    /// <summary>Whether there is anything to show yet, the render loop has to have come round once.</summary>
    public bool Ready;

    /* The frames */

    public double Fps, FpsTarget, WorkMs, PeakWorkMs, JitterMs, GpuMs, RenderLoad, Low1Fps, P99Ms, WorstGapMs;
    public int Stutters;
    public LoopStatistics.LoopState RenderState;

    /// <summary>How long a frame is from one to the next, in milliseconds.</summary>
    public double FrameMs => Fps > 0.0 ? 1000.0 / Fps : 0.0;

    /// <summary>How much of a frame the GPU is busy for, from 0 to 1.</summary>
    public double GpuLoad => GpuMs * Fps * 0.001;

    /// <summary>What a frame may take before it counts as slow, the limit on the frames if there is one and sixty a second if not.</summary>
    public double FrameBudgetMs => 1000.0 / (FpsTarget > 0.0 ? FpsTarget : 60.0);

    /* The simulation */

    public bool HasSimulation, HasLogic, HasPhysics;
    public LoopStatistics.LoopState SimulationState;
    public double TickRate, TickTarget, TickMs, TickLoad, TickWaitMs;
    public double LogicRate, LogicMs, LogicPeakMs, PhysicsRate, PhysicsMs, PhysicsPeakMs;
    public long Dropped, Late, Tick;

    /// <summary>Whether ticks were dropped since the read before this one, which is the simulation not keeping up right now.</summary>
    public bool Dropping;

    public bool Interpolated;
    public double BehindMs, TickGapMs;

    /// <summary>What a tick may take, in milliseconds.</summary>
    public double TickBudgetMs => TickTarget > 0.0 ? 1000.0 / TickTarget : 0.0;

    /* The GPU's passes, the lines they get in the order they are shown, and what all of the frame's own come to */

    public readonly Pass[] Passes = new Pass[MAX_PASSES];
    public readonly int[] Rows = new int[MAX_ROWS];
    public int PassCount, RowCount;
    public double PassTotal;

    /* The device and the memory */

    public GraphicsStatistics.Frame Frame;
    public long GpuMemory;
    public int Blocks, Textures, Buffers, Pipelines;
    public double GarbagePerSecond, RenderGarbage, SimulationGarbage, HeapBytes;
    public int Small, Mid, Big;

    /* The last twelve seconds of the frames and of the ticks, and how many of the slices are filled */

    public readonly LoopSlice[] Frames = new LoopSlice[LoopStatistics.TIMELINE];
    public readonly LoopSlice[] Ticks = new LoopSlice[LoopStatistics.TIMELINE];
    public int FrameSlices, TickSlices;

    /// <summary>What a frame and a tick usually take in those twelve seconds, in milliseconds, which is what the graphs are scaled by.</summary>
    public float FrameUsualMs, TickUsualMs;

    // Where the slices are put in order to find the one in the middle
    private readonly float[] sorting = new float[LoopStatistics.TIMELINE];

    private LoopStatistics? render, simulation;
    private long lastAllocated, lastDropped;
    private double lastReadAt;

    /// <summary>
    /// Reads the numbers. Simulation thread.
    /// </summary>
    public void Read(GameEngine engine)
    {
        var window = engine.WindowManager;
        var loops = window.Loops;

        render = Find(loops, "Render");
        simulation = Find(loops, "Simulation");
        LoopStatistics? logic = Find(loops, "Logic"), physics = Find(loops, "Physics");

        Ready = render is not null;
        GpuMs = engine.GpuFrameMs;

        if (render is not null)
        {
            render.Instability(out double p99, out double worstPercent, out int stutters);

            Fps = render.Rate;
            FpsTarget = render.TargetRate;
            WorkMs = render.WorkMs;
            PeakWorkMs = render.PeakWorkMs;
            JitterMs = render.JitterMs;
            RenderLoad = render.Load;
            RenderState = render.State;
            Low1Fps = worstPercent > 0.0 ? 1000.0 / worstPercent : 0.0;
            P99Ms = p99;
            Stutters = stutters;
        }

        HasSimulation = simulation is not null;
        if (simulation is not null)
        {
            TickRate = simulation.Rate;
            TickTarget = simulation.TargetRate;
            TickMs = simulation.WorkMs;
            TickLoad = simulation.Load;
            TickWaitMs = simulation.WaitMs;
            SimulationState = simulation.State;
            Late = simulation.LateTurns;
            Dropped = simulation.DroppedTurns;
            Dropping = Dropped > lastDropped;
            lastDropped = Dropped;
        }

        HasLogic = logic is not null;
        if (logic is not null)
            (LogicRate, LogicMs, LogicPeakMs) = (logic.Rate, logic.WorkMs, logic.PeakWorkMs);

        HasPhysics = physics is not null;
        if (physics is not null)
            (PhysicsRate, PhysicsMs, PhysicsPeakMs) = (physics.Rate, physics.WorkMs, physics.PeakWorkMs);

        var clock = window.Snapshots;
        Interpolated = window.Presentation == PresentationMode.Interpolated;
        BehindMs = clock.Delay * 1000.0;
        TickGapMs = clock.TickInterval * 1000.0;
        Tick = window.Tick;

        var device = engine.Graphics;
        ReadPasses(device.GpuScopes);

        Frame = device.Statistics.Last;
        GpuMemory = device.Statistics.MemoryInUse;
        Blocks = device.Statistics.MemoryBlocks;
        Textures = device.Statistics.Textures;
        Buffers = device.Statistics.Buffers;
        Pipelines = device.Statistics.Pipelines;

        // Garbage made since the last look, whoever made it (the loops only know their own)
        long allocated = GC.GetTotalAllocatedBytes(false);
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        GarbagePerSecond = lastReadAt > 0.0 ? (allocated - lastAllocated) / Math.Max(0.001, now - lastReadAt) : 0.0;
        lastAllocated = allocated;
        lastReadAt = now;

        RenderGarbage = render?.AllocatedPerSecond ?? 0.0;
        SimulationGarbage = simulation?.AllocatedPerSecond ?? 0.0;
        HeapBytes = GC.GetTotalMemory(false);
        Small = GC.CollectionCount(0);
        Mid = GC.CollectionCount(1);
        Big = GC.CollectionCount(2);

        ReadTimelines();
    }

    /// <summary>
    /// Reads the timelines, which move on a slice every tenth of a second and so are read more often than the
    /// numbers, a graph that hops four times a second looks like it is thinking about it. Simulation thread.
    /// </summary>
    public void ReadTimelines()
    {
        FrameSlices = render?.CopyTimeline(Frames) ?? 0;
        TickSlices = simulation?.CopyTimeline(Ticks) ?? 0;

        // The longest anybody waited for a frame in all of it, the one number a hitch can't hide from
        float worst = 0.0f;
        for (int i = Frames.Length - FrameSlices; i < Frames.Length; i++)
            worst = MathF.Max(worst, Frames[i].WorstGapMs);

        WorstGapMs = worst;

        FrameUsualMs = Usual(Frames, FrameSlices, gaps: true);
        TickUsualMs = Usual(Ticks, TickSlices, gaps: false);
    }

    /// <summary>
    /// Helper method to find what a turn usually took over a timeline, the slice in the middle once they are put in
    /// order. Not the average. A scene that took a third of a second to load is five slices of three hundred
    /// milliseconds, and the average of those and a hundred and fifteen slices of one and a half is nothing a
    /// frame ever took.
    /// </summary>
    private float Usual(LoopSlice[] slices, int filled, bool gaps)
    {
        if (filled == 0)
            return 0.0f;

        int first = slices.Length - filled;
        for (int i = 0; i < filled; i++)
            sorting[i] = gaps ? slices[first + i].GapMs : slices[first + i].WorkMs;

        Array.Sort(sorting, 0, filled);
        return sorting[filled / 2];
    }

    /// <summary>
    /// Helper method to add the stretches of the frame up by their names. A frame names the same thing as often as
    /// it does it (four particle renderers, four UIs), and four lines saying "particles" with a number each is the
    /// wall of text this overlay used to be.
    /// </summary>
    internal void ReadPasses(ReadOnlySpan<GpuScope> scopes)
    {
        PassCount = 0;
        PassTotal = 0.0;

        int parent = -1;
        foreach (ref readonly GpuScope scope in scopes)
        {
            if (scope.Depth > 1 || scope.Name is null)
                continue;

            bool own = scope.Depth == 0;
            if (!own && parent < 0)
                continue;

            int at = FindPass(scope.Name, own ? -1 : parent);
            if (at < 0)
            {
                // No room for it. What it is made of has nowhere to go then either
                if (PassCount == MAX_PASSES)
                {
                    if (own) parent = -1;
                    continue;
                }

                at = PassCount++;
                Passes[at] = new Pass { Name = scope.Name, Parent = own ? -1 : parent };
            }

            Passes[at].Milliseconds += scope.Milliseconds;
            Passes[at].Count++;

            if (own)
            {
                parent = at;
                PassTotal += scope.Milliseconds;
            }
        }

        ChooseRows();
    }

    private int FindPass(string name, int parent)
    {
        for (int i = 0; i < PassCount; i++)
        {
            if (Passes[i].Parent == parent && Passes[i].Name == name)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Helper method to pick the passes that get a line, the dearest of the frame's own first and then, with the
    /// lines that are left, the dearest of what those are made of. They are listed in the order of the frame all
    /// the same, sorted by what they cost two passes that cost about the same would swap places four times a second.
    /// </summary>
    private void ChooseRows()
    {
        int top = 0;
        for (; top < MAX_TOP; top++)
        {
            if (Dearest(own: true) is not { } pick) break;
            Passes[pick].Shown = true;
        }

        for (int left = MAX_ROWS - top; left > 0; left--)
        {
            if (Dearest(own: false) is not { } pick) break;
            Passes[pick].Shown = true;
        }

        RowCount = 0;
        for (int i = 0; i < PassCount; i++)
        {
            if (Passes[i].Parent >= 0 || !Passes[i].Shown)
                continue;

            Rows[RowCount++] = i;
            for (int part = i + 1; part < PassCount; part++)
            {
                if (Passes[part].Parent == i && Passes[part].Shown)
                    Rows[RowCount++] = part;
            }
        }

        for (int row = 0; row < RowCount; row++)
            Passes[Rows[row]].Colour = row;
    }

    /// <summary>Helper method to find the dearest pass that has no line yet, of the frame's own or of what the ones with a line are made of.</summary>
    private int? Dearest(bool own)
    {
        int? best = null;
        for (int i = 0; i < PassCount; i++)
        {
            ref readonly Pass pass = ref Passes[i];
            if (pass.Shown || pass.Milliseconds < SMALL || pass.Parent < 0 != own)
                continue;

            if (!own && !Passes[pass.Parent].Shown)
                continue;

            if (best is not { } so || pass.Milliseconds > Passes[so].Milliseconds)
                best = i;
        }

        return best;
    }

    private static LoopStatistics? Find(IReadOnlyList<LoopStatistics> loops, string name)
    {
        // By the number, walking a list through its interface makes an enumerator every time
        for (int i = 0; i < loops.Count; i++)
        {
            if (loops[i].Name == name)
                return loops[i];
        }

        return null;
    }
}
