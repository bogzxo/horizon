using Horizon.Core.Threading;
using Horizon.Graphics;
using Horizon.UI;

namespace Horizon.Tests;

public class PerformanceOverlayTests
{
    /* The timeline the graphs are drawn out of */

    [Fact]
    public void A_steady_loop_is_a_slice_every_tenth_of_a_second()
    {
        var loop = new LoopStatistics("test", 120.0);

        // Ten seconds of ticks a hundred and twentieth of a second apart, a millisecond of work each
        for (int turn = 0; turn < 1200; turn++)
            loop.Record(0.001, 0.0, 1.0 / 120.0);

        var slices = new LoopSlice[LoopStatistics.TIMELINE];
        int filled = loop.CopyTimeline(slices);

        Assert.InRange(filled, 98, 100);

        // What there is none of yet is at the start, the newest is the last one
        Assert.Equal(default, slices[0]);
        for (int i = slices.Length - filled; i < slices.Length; i++)
        {
            Assert.InRange(slices[i].Turns, 12, 13);
            Assert.Equal(1000.0f / 120.0f, slices[i].GapMs, 2);
            Assert.Equal(1000.0f / 120.0f, slices[i].WorstGapMs, 2);
            Assert.Equal(1.0f, slices[i].WorkMs, 3);
            Assert.Equal(1.0f, slices[i].WorstWorkMs, 3);
        }
    }

    [Fact]
    public void A_hitch_is_as_wide_on_the_timeline_as_it_was_long()
    {
        var loop = new LoopStatistics("test", 0.0);

        for (int turn = 0; turn < 600; turn++)
            loop.Record(0.0005, 0.0, 0.001);

        // Half a second of nothing, a scene loading say
        loop.Record(0.48, 0.0, 0.5);

        for (int turn = 0; turn < 250; turn++)
            loop.Record(0.0005, 0.0, 0.001);

        var slices = new LoopSlice[LoopStatistics.TIMELINE];
        int filled = loop.CopyTimeline(slices);

        int wide = 0;
        for (int i = slices.Length - filled; i < slices.Length; i++)
        {
            if (slices[i].WorstGapMs > 400.0f) wide++;
        }

        // Five tenths of a second, so a second on the graph stays a second
        Assert.Equal(5, wide);

        // And the newest slices are back to what they were
        Assert.Equal(1.0f, slices[^1].WorstGapMs, 2);
    }

    [Fact]
    public void A_shorter_buffer_gets_the_newest_slices()
    {
        var loop = new LoopStatistics("test", 0.0);

        // A slice of slow turns, then slices of quick ones
        for (int turn = 0; turn < 10; turn++) loop.Record(0.009, 0.0, 0.01);
        for (int turn = 0; turn < 300; turn++) loop.Record(0.0005, 0.0, 0.001);

        var newest = new LoopSlice[2];
        Assert.Equal(2, loop.CopyTimeline(newest));
        Assert.Equal(0.5f, newest[0].WorkMs, 2);
        Assert.Equal(0.5f, newest[1].WorkMs, 2);
    }

    /* The top of a graph */

    [Theory]
    [InlineData(0.7f, 0.75f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(3.71f, 5.0f)]
    [InlineData(20.8f, 30.0f)]
    [InlineData(41.7f, 50.0f)]
    [InlineData(160.0f, 200.0f)]
    public void The_top_of_a_graph_is_the_next_round_number_up(float value, float top) =>
        Assert.Equal(top, PerformanceBoard.Ceiling(value), 4);

    /* The passes of the GPU */

    [Fact]
    public void The_same_name_is_one_pass_however_often_the_frame_did_it()
    {
        var sample = new PerformanceSample();
        sample.ReadPasses(
        [
            new GpuScope("Renderer2D", 1.30, 0),
            new GpuScope("DeferredRenderer2D", 0.90, 1),
            new GpuScope("ui", 0.02, 1),
            new GpuScope("deep down", 0.50, 2),
            new GpuScope("particles", 0.05, 0),
            new GpuScope("particles", 0.02, 0),
            new GpuScope("ui", 0.01, 0),
            new GpuScope("particles", 0.01, 0),
            new GpuScope("ui", 0.01, 0),
        ]);

        // Three of the frame's own and the two things the first is made of, in the order of the frame
        string[] names = new string[sample.RowCount];
        for (int row = 0; row < sample.RowCount; row++)
            names[row] = sample.Passes[sample.Rows[row]].Name;

        Assert.Equal(["Renderer2D", "DeferredRenderer2D", "ui", "particles", "ui"], names);

        var particles = sample.Passes[sample.Rows[3]];
        Assert.Equal(3, particles.Count);
        Assert.Equal(0.08, particles.Milliseconds, 6);
        Assert.Equal(-1, particles.Parent);

        // The ui inside of the renderer is not the ui after it
        var inside = sample.Passes[sample.Rows[2]];
        var after = sample.Passes[sample.Rows[4]];
        Assert.Equal(sample.Rows[0], inside.Parent);
        Assert.Equal(1, inside.Count);
        Assert.Equal(-1, after.Parent);
        Assert.Equal(2, after.Count);

        // What the frame comes to is its own passes, not those and what they are made of on top
        Assert.Equal(1.30 + 0.08 + 0.02, sample.PassTotal, 6);
    }

    [Fact]
    public void Only_the_dearest_get_a_line_and_every_line_a_colour_of_its_own()
    {
        var sample = new PerformanceSample();

        // Ten passes, dearer as they go, and one that cost next to nothing
        var scopes = new GpuScope[11];
        for (int i = 0; i < 10; i++) scopes[i] = new GpuScope($"pass {i}", 0.1 * (i + 1), 0);
        scopes[10] = new GpuScope("nothing", 0.0001, 0);

        sample.ReadPasses(scopes);

        Assert.Equal(PerformanceSample.MAX_TOP, sample.RowCount);
        for (int row = 0; row < sample.RowCount; row++)
        {
            var pass = sample.Passes[sample.Rows[row]];

            // The six dearest are the last six, still in the order they came, each with the colour of its line
            Assert.Equal($"pass {row + 4}", pass.Name);
            Assert.Equal(row, pass.Colour);
        }

        // Read again the lines are the same ones, nothing is left over from the read before
        sample.ReadPasses(scopes);
        Assert.Equal(PerformanceSample.MAX_TOP, sample.RowCount);
        Assert.Equal("pass 4", sample.Passes[sample.Rows[0]].Name);
    }

    [Fact]
    public void A_frame_with_nothing_named_has_no_lines()
    {
        var sample = new PerformanceSample();
        sample.ReadPasses([]);

        Assert.Equal(0, sample.RowCount);
        Assert.Equal(0.0, sample.PassTotal);
    }
}
