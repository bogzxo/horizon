using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI;

// The sections of the card. Each one says how tall it is, and handed a list draws itself from the top of the area
// it is given. Asked without a list it only says how tall, which is what the measuring is done with.
internal sealed partial class PerformanceBoard
{
    /// <summary>
    /// The frame rate, as big as anything in here gets, and next to it what a frame costs the CPU and the GPU with
    /// how much of the frame each is busy for. Whichever of the two bars is full is what the frames are waiting on.
    /// </summary>
    private float Header(UIDrawList? list, UIRect area)
    {
        const float HEIGHT = 50.0f, TILE = 96.0f;
        if (list is null)
            return HEIGHT;

        var sample = Sample;
        float left = area.Min.X, top = area.Max.Y, right = area.Max.X;
        part = HEADER_PARTS;

        if (!sample.Ready)
        {
            Words(list, "starting up", Box(left, top, COLUMN, HEIGHT), Origin.Left, large, Plain);
            return HEIGHT;
        }

        // White while the frames do what they are meant to, the colour of the trouble they are in when they don't
        Vector4 health = Health();
        float line = skin.Font.LineHeight;

        ink.Start().Add(Math.Min(sample.Fps, 99999.0), "0");
        float width = Width(ink.Text, huge);
        float lifted = MathF.Round(line * huge * 0.14f);
        Words(list, ink.Text, new Vector2(left - 1.0f, top + lifted), huge, health == Good ? Bright : health);

        // On the same line as the number, which is a lot further down a big letter than a small one
        float under = top + lifted - line * huge;
        Words(list, "fps", new Vector2(left + width + 6.0f, under + line * small + MathF.Round(line * (huge - small) * 0.2f)), small, Dim);

        ink.Start().Add(sample.FrameMs, sample.FrameMs < 99.95 ? "0.00" : "0.0").Add(" ms a frame");
        Words(list, ink.Text, Box(left, top - 36.0f, 160.0f, 14.0f), Origin.Left, small, Dim);

        float tiles = left + 150.0f;

        // Never more than all of a frame, whatever two numbers that are each smoothed their own way come to
        part = HEADER_PARTS + 100;
        Tile(list, tiles, top, TILE, "CPU", sample.WorkMs, Math.Min(sample.RenderLoad, 1.0));

        part = HEADER_PARTS + 200;
        if (sample.GpuMs > 0.0)
            Tile(list, tiles + TILE + 14.0f, top, TILE, "GPU", sample.GpuMs, Math.Min(sample.GpuLoad, 1.0));

        part = HEADER_PARTS + 300;
        if (Key.Length > 0)
        {
            float cap = Cap(null, 0.0f, 0.0f);
            Cap(list, right - cap, top + 2.0f);
        }

        return HEIGHT;
    }

    /// <summary>Helper method to draw what a frame costs one side of the machine, with a bar of how much of the frame that is.</summary>
    private void Tile(UIDrawList list, float left, float top, float width, string name, double milliseconds, double load)
    {
        var label = Box(left, top, width, 13.0f);
        Words(list, name, label, Origin.Left, small, Dim);

        ink.Start().Add(Math.Clamp(load, 0.0, 9.99) * 100.0, "0").Add("%");
        Words(list, ink.Text, label, Origin.Right, small, Dim);

        ink.Start().Add(milliseconds, milliseconds < 99.95 ? "0.00" : "0.0");
        Value(list, Box(left, top - 14.0f, width, 22.0f), Origin.Left, ink.Text, "ms", large, Bright);

        Meter(list, Box(left, top - 42.0f, width, 4.0f), load, Accent);
    }

    /// <summary>
    /// The last twelve seconds of frames, and how uneven they have been. A frame rate is only as good as its worst
    /// frames, so those get numbers of their own under the graph.
    /// </summary>
    private float Frame(UIDrawList? list, UIRect area)
    {
        const float GRAPH = 54.0f;
        const float HEIGHT = TITLE + GRAPH + 8.0f + CELL;
        if (list is null)
            return HEIGHT;

        var sample = Sample;
        float left = area.Min.X, top = area.Max.Y;
        double budget = sample.FrameBudgetMs;
        part = FRAME_PARTS;

        Title(list, left, top, "FRAME TIME", "the last 12 seconds");
        top -= TITLE;

        // The line is where the frames are meant to stay under, there is none for frames nobody set a limit on
        Timeline(list, Box(left, top, COLUMN, GRAPH), sample.Frames, sample.FrameSlices, gaps: true, sample.FrameUsualMs, sample.FpsTarget > 0.0 ? budget : 0.0, budget, FRAME_PARTS + 100);
        top -= GRAPH + 8.0f;

        part = FRAME_PARTS + 500;

        float cell = COLUMN / 5.0f;
        Cell(list, left, top, cell, "1% low", sample.Low1Fps, "0", "fps", Bright);
        Cell(list, left + cell, top, cell, "p99", sample.P99Ms, "0.0", "ms", Bright);
        Cell(list, left + cell * 2.0f, top, cell, "worst", sample.WorstGapMs, "0.0", "ms", Against(sample.WorstGapMs, budget, Bright));
        Cell(list, left + cell * 3.0f, top, cell, "jitter", sample.JitterMs, "0.00", "ms", Bright);
        Cell(list, left + cell * 4.0f, top, cell, "stutters", sample.Stutters, "0", string.Empty, sample.Stutters > 0 ? Warn : Bright);

        return HEIGHT;
    }

    /// <summary>
    /// What every thread of the engine is doing right now, how often it comes round, what a turn costs it and how
    /// much of its time that is. Then the ticks of the last twelve seconds against what a tick may take, and what
    /// went wrong with them, if anything did.
    /// </summary>
    private float Threads(UIDrawList? list, UIRect area)
    {
        const float GRAPH = 34.0f;

        var sample = Sample;
        int rows = 1 + (sample.HasSimulation ? 1 : 0) + (sample.HasLogic ? 1 : 0) + (sample.HasPhysics ? 1 : 0);
        float height = TITLE + rows * ROW + (sample.HasSimulation ? 7.0f + GRAPH + 5.0f + ROW : 0.0f);
        if (list is null)
            return height;

        float left = area.Min.X, top = area.Max.Y;
        part = THREAD_PARTS;

        // How far in the past the frames show, which is what drawing between two ticks costs
        tail.Start().Add(sample.Interpolated ? "interpolated, " : "newest tick, ").Add(sample.BehindMs, "0.0").Add(" ms behind");
        Title(list, left, top, "THREADS", sample.HasSimulation ? tail.Text : default);
        top -= TITLE;

        // Every line starts at a number of its own, a thread that isn't there leaves the ones under it alone
        part = THREAD_PARTS + 40;
        Thread(list, left, top, "render", sample.RenderState, sample.Fps, sample.FpsTarget, sample.WorkMs, Math.Min(sample.RenderLoad, 1.0), Bright, Accent);
        top -= ROW;

        if (sample.HasSimulation)
        {
            // Not keeping up is the one thing in here that is always bad news
            bool behind = sample.TickTarget > 0.0 && sample.TickRate < sample.TickTarget * 0.97;
            Vector4 rate = sample.Dropping ? Bad : behind ? Warn : Bright;
            Vector4 bar = sample.TickLoad >= 1.0 ? Bad : sample.TickLoad >= 0.8 ? Warn : Accent;

            part = THREAD_PARTS + 80;
            Thread(list, left, top, "simulation", sample.SimulationState, sample.TickRate, sample.TickTarget, sample.TickMs, sample.TickLoad, rate, bar);
            top -= ROW;
        }

        if (sample.HasLogic)
        {
            part = THREAD_PARTS + 120;
            Part(list, left, top, "logic", sample.LogicRate, sample.LogicMs, sample.LogicPeakMs);
            top -= ROW;
        }

        if (sample.HasPhysics)
        {
            part = THREAD_PARTS + 160;
            Part(list, left, top, "physics", sample.PhysicsRate, sample.PhysicsMs, sample.PhysicsPeakMs);
            top -= ROW;
        }

        if (!sample.HasSimulation)
            return height;

        top -= 7.0f;
        Timeline(list, Box(left, top, COLUMN, GRAPH), sample.Ticks, sample.TickSlices, gaps: false, sample.TickUsualMs, sample.TickBudgetMs, sample.TickBudgetMs, THREAD_PARTS + 200);
        top -= GRAPH + 5.0f;

        part = THREAD_PARTS + 600;

        float x = Inline(list, left, top, "waits", sample.TickWaitMs, "0.00", "ms", Plain);
        x = Inline(list, x, top, "late", sample.Late, "0", string.Empty, Plain);
        x = Inline(list, x, top, "dropped", sample.Dropped, "0", string.Empty, sample.Dropping ? Bad : sample.Dropped > 0 ? Warn : Plain);

        ink.Start().Add("tick ").Add(sample.Tick);
        Words(list, ink.Text, Box(left, top, COLUMN, ROW), Origin.Right, small, Dim);

        return height;
    }

    /// <summary>Helper method to draw the line of a thread, see <see cref="Threads"/> for what is on it.</summary>
    private void Thread(UIDrawList list, float left, float top, string name, LoopStatistics.LoopState state, double rate, double target, double milliseconds, double load, Vector4 rateColour, Vector4 barColour)
    {
        Vector4 dot = state switch
        {
            LoopStatistics.LoopState.Working => Good,
            LoopStatistics.LoopState.Sleeping => Accent,
            LoopStatistics.LoopState.Blocked => Warn,
            _ => Dim
        };

        Round(list, Box(left + 1.0f, top - (ROW - 6.0f) * 0.5f, 6.0f, 6.0f), 3.0f, dot);
        Words(list, name, Box(left + 15.0f, top, 90.0f, ROW), Origin.Left, text, Plain);
        Words(list, Doing(state), Box(left + 104.0f, top, 70.0f, ROW), Origin.Left, small, Dim);

        // Out of how many it is meant to be, for one that is meant to be anything
        ink.Start().Add(rate, "0");
        tail.Start().Add("/");
        if (target > 0.0) tail.Add(target, "0");
        else tail.Add("s");
        Value(list, Box(left + 160.0f, top, 76.0f, ROW), Origin.Right, ink.Text, tail.Text, text, rateColour);

        ink.Start().Add(milliseconds, "0.00");
        Value(list, Box(left + 236.0f, top, 68.0f, ROW), Origin.Right, ink.Text, "ms", text, Bright);

        Meter(list, Box(left + 316.0f, top - (ROW - 4.0f) * 0.5f, 50.0f, 4.0f), load, barColour);

        ink.Start().Add(Math.Clamp(load, 0.0, 9.99) * 100.0, "0").Add("%");
        Words(list, ink.Text, Box(left + 366.0f, top, 38.0f, ROW), Origin.Right, small, Dim);
    }

    /// <summary>Helper method to draw the line of something a thread does every turn (the logic, the physics), under the thread and a bit in.</summary>
    private void Part(UIDrawList list, float left, float top, string name, double rate, double milliseconds, double peak)
    {
        Words(list, name, Box(left + 27.0f, top, 90.0f, ROW), Origin.Left, text, Dim);

        ink.Start().Add(rate, "0");
        Value(list, Box(left + 160.0f, top, 76.0f, ROW), Origin.Right, ink.Text, "/s", text, Plain);

        ink.Start().Add(milliseconds, "0.00");
        Value(list, Box(left + 236.0f, top, 68.0f, ROW), Origin.Right, ink.Text, "ms", text, Plain);

        ink.Start().Add("worst ").Add(peak, "0.0");
        Value(list, Box(left + 304.0f, top, 100.0f, ROW), Origin.Right, ink.Text, "ms", small, Dim);
    }

    /// <summary>What a thread is up to, in a word.</summary>
    private static string Doing(LoopStatistics.LoopState state) => state switch
    {
        LoopStatistics.LoopState.Working => "working",
        LoopStatistics.LoopState.Sleeping => "sleeping",
        LoopStatistics.LoopState.Blocked => "held up",
        _ => "stopped"
    };

    /// <summary>
    /// What the frame cost the GPU, pass by pass. One bar cut up by how much of the frame each of them took and a
    /// line for each under it in the same colour, with what the dearer ones are made of. A pass that is made of
    /// others is those on the bar and, in its own colour, whatever of it they don't account for. One renderer that
    /// is ninety nine parts of the frame in a hundred makes for a bar that says fuck all otherwise. The same name is
    /// one line however often the frame did it. They are the stretches the renderers name
    /// (<see cref="Graphics.GraphicsDevice.BeginGpuScope"/>), a frame or two behind the CPU.
    /// </summary>
    private float Gpu(UIDrawList? list, UIRect area)
    {
        const float BAR = 8.0f, LINE = 16.0f;

        var sample = Sample;
        rowsHeld = Math.Max(rowsHeld, Math.Max(1, sample.RowCount));

        float height = TITLE + BAR + 8.0f + rowsHeld * LINE;
        if (list is null)
            return height;

        float left = area.Min.X, top = area.Max.Y;
        double whole = Math.Max(sample.GpuMs, sample.PassTotal);
        part = GPU_PARTS;

        tail.Start();
        if (whole > 0.0) tail.Add(whole, "0.00").Add(" ms a frame");
        Title(list, left, top, "GPU", tail.Text);
        top -= TITLE;

        Round(list, Box(left, top, COLUMN, BAR), 3.0f, WellColour);
        if (whole > 0.0)
        {
            double along = 0.0;
            for (int row = 0; row < sample.RowCount; row++)
            {
                int index = sample.Rows[row];
                ref readonly var pass = ref sample.Passes[index];

                // What it is made of comes straight after it in the lines, and before what is left of it on the bar.
                // Every piece is named by the line it belongs to, so one too thin to draw doesn't rename the rest
                double pieces = 0.0;
                while (row + 1 < sample.RowCount && sample.Passes[sample.Rows[row + 1]].Parent == index)
                {
                    ref readonly var piece = ref sample.Passes[sample.Rows[++row]];
                    part = GPU_PARTS + 50 + piece.Colour;
                    Piece(list, left, top, BAR, ref along, piece.Milliseconds, whole, Colour(piece.Colour));
                    pieces += piece.Milliseconds;
                }

                part = GPU_PARTS + 50 + pass.Colour;
                Piece(list, left, top, BAR, ref along, Math.Max(0.0, pass.Milliseconds - pieces), whole, Colour(pass.Colour));
            }
        }

        top -= BAR + 8.0f;

        part = GPU_PARTS + 90;
        if (sample.RowCount == 0)
        {
            Words(list, "the GPU has not said what its passes cost", Box(left, top, COLUMN, LINE), Origin.Left, small, Dim);
            return height;
        }

        float letter = Width("0", text);
        for (int row = 0; row < sample.RowCount; row++, top -= LINE)
        {
            ref readonly var pass = ref sample.Passes[sample.Rows[row]];
            bool own = pass.Parent < 0;
            part = GPU_PARTS + 100 + row * 20;

            // A chip of its colour on the bar, a bit in for what is a part of the pass above it
            float indent = own ? 15.0f : 28.0f;
            Round(list, Box(left + indent - 14.0f, top - (LINE - 8.0f) * 0.5f, 8.0f, 8.0f), 2.0f, Colour(pass.Colour));

            // As much of the name as there is room for. They are short, the odd long one is cut off and no harm done
            ReadOnlySpan<char> name = pass.Name;
            int room = Math.Max(1, (int)((262.0f - indent) / letter));
            if (name.Length > room) name = name[..room];

            Words(list, name, Box(left + indent, top, 262.0f - indent, LINE), Origin.Left, text, own ? Plain : Dim);

            if (pass.Count > 1)
            {
                ink.Start().Add("x").Add(pass.Count);
                Words(list, ink.Text, Box(left + indent + name.Length * letter + 7.0f, top, 40.0f, LINE), Origin.Left, small, Dim);
            }

            ink.Start().Add(pass.Milliseconds, "0.00");
            Value(list, Box(left + 270.0f, top, 80.0f, LINE), Origin.Right, ink.Text, "ms", text, own ? Bright : Plain);

            if (whole > 0.0)
            {
                ink.Start().Add(pass.Milliseconds / whole * 100.0, "0").Add("%");
                Words(list, ink.Text, Box(left + 356.0f, top, 48.0f, LINE), Origin.Right, small, own ? Plain : Dim);
            }
        }

        return height;
    }

    /// <summary>Helper method to draw the next piece of the bar of the GPU's passes and move on along it.</summary>
    private void Piece(UIDrawList list, float left, float top, float height, ref double along, double milliseconds, double whole, Vector4 colour)
    {
        // A pixel of nothing between one and the next, and one too thin to see is left out
        float from = MathF.Round((float)(along / whole) * COLUMN);
        along += milliseconds;
        float to = MathF.Round((float)(along / whole) * COLUMN) - 1.0f;

        if (to - from >= 2.0f)
            Round(list, new UIRect(new Vector2(left + from, top - height), new Vector2(left + to, top)), 2.0f, colour);
    }

    private static Vector4 Colour(int index) => PassColours[index % PassColours.Length];

    /// <summary>What the last frame asked of the device, the things that add up when there are too many of them.</summary>
    private float Device(UIDrawList? list, UIRect area)
    {
        const float HEIGHT = TITLE + CELL * 2.0f + 3.0f;
        if (list is null)
            return HEIGHT;

        var frame = Sample.Frame;
        float left = area.Min.X, top = area.Max.Y, cell = COLUMN / 4.0f;
        part = DEVICE_PARTS;

        Title(list, left, top, "DEVICE", "the last frame");
        top -= TITLE;

        Cell(list, left, top, cell, "draws", frame.DrawCalls, "0", string.Empty, Bright);
        Cell(list, left + cell, top, cell, "instances", frame.Instances, "0", string.Empty, Bright);
        Cell(list, left + cell * 2.0f, top, cell, "dispatches", frame.Dispatches, "0", string.Empty, Bright);
        Cell(list, left + cell * 3.0f, top, cell, "barriers", frame.Barriers, "0", string.Empty, Bright);
        top -= CELL + 3.0f;

        Cell(list, left, top, cell, "pipelines", frame.PipelineBinds, "0", string.Empty, Bright);
        Cell(list, left + cell, top, cell, "sets", frame.DescriptorSets, "0", string.Empty, Bright);
        Cell(list, left + cell * 2.0f, top, cell, "targets", frame.RenderTargetBinds, "0", string.Empty, Bright);

        double sent = frame.BytesUploaded + frame.BytesStreamed;
        Cell(list, left + cell * 3.0f, top, cell, "sent up", Amount(sent, out string unit), sent < 1024.0 * 1024.0 ? "0" : "0.0", unit, Bright);

        return HEIGHT;
    }

    /// <summary>
    /// What the GPU is holding and what the garbage collector has to deal with. Garbage that never stops climbing
    /// is what ends up as a hitch when the collector cleans up after it, so who is making it has a number each.
    /// </summary>
    private float Memory(UIDrawList? list, UIRect area)
    {
        const float HEIGHT = TITLE + CELL * 2.0f + 3.0f + 4.0f + ROW;
        if (list is null)
            return HEIGHT;

        var sample = Sample;
        float left = area.Min.X, top = area.Max.Y, cell = COLUMN / 4.0f;
        part = MEMORY_PARTS;

        Title(list, left, top, "MEMORY", default);
        top -= TITLE;

        tail.Start().Add("gpu, ").Add(sample.Blocks).Add(sample.Blocks == 1 ? " block" : " blocks");
        Cell(list, left, top, cell, tail.Text, Amount(sample.GpuMemory, out string unit), "0.0", unit, Bright);
        Cell(list, left + cell, top, cell, "textures", sample.Textures, "0", string.Empty, Bright);
        Cell(list, left + cell * 2.0f, top, cell, "buffers", sample.Buffers, "0", string.Empty, Bright);
        Cell(list, left + cell * 3.0f, top, cell, "pipelines", sample.Pipelines, "0", string.Empty, Bright);
        top -= CELL + 3.0f;

        Cell(list, left, top, cell, "heap", Amount(sample.HeapBytes, out unit), "0.0", unit, Bright);
        Cell(list, left + cell, top, cell, "garbage", Rate(sample.GarbagePerSecond, out unit), "0", unit, Bright);
        Cell(list, left + cell * 2.0f, top, cell, "by the frames", Rate(sample.RenderGarbage, out unit), "0", unit, Plain);
        Cell(list, left + cell * 3.0f, top, cell, "by the ticks", Rate(sample.SimulationGarbage, out unit), "0", unit, Plain);
        top -= CELL + 3.0f + 4.0f;

        Words(list, "collections", Box(left, top, cell, ROW), Origin.Left, small, Dim);
        float x = Inline(list, left + cell, top, "small", sample.Small, "0", string.Empty, Plain);
        x = Inline(list, x, top, "mid", sample.Mid, "0", string.Empty, Plain);
        Inline(list, x, top, "big", sample.Big, "0", string.Empty, Plain);

        return HEIGHT;
    }

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
