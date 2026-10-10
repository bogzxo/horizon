using Horizon.Rendering.Lighting;

namespace Horizon.Tests;

public class PathTracingTests
{
    [Fact]
    public void The_rays_are_turned_within_a_cone_and_any_few_frames_spread_over_it()
    {
        const float DIRECTIONS = 4.0f;
        float cone = MathF.Tau / DIRECTIONS;

        var turns = new float[8];
        for (uint frame = 0; frame < turns.Length; frame++)
        {
            turns[frame] = PathTracedLighting2D.Turn(frame, DIRECTIONS);
            Assert.InRange(turns[frame], 0.0f, cone);
        }

        // Eight frames in a row leave no gap in the cone wider than a quarter of it, which random numbers would
        Array.Sort(turns);
        float widest = turns[0] + cone - turns[^1];
        for (int i = 1; i < turns.Length; i++) widest = MathF.Max(widest, turns[i] - turns[i - 1]);

        Assert.True(widest < cone * 0.25f, $"a gap of {widest / cone:0.00} of the cone");
    }

    [Fact]
    public void The_probes_are_moved_all_over_a_cell_and_eight_frames_cover_its_quarters()
    {
        var quarters = new bool[4];
        for (uint frame = 0; frame < 8; frame++)
        {
            var nudge = PathTracedLighting2D.Nudge(frame);
            Assert.InRange(nudge.X, 0.0f, 1.0f);
            Assert.InRange(nudge.Y, 0.0f, 1.0f);
            quarters[(nudge.X < 0.5f ? 0 : 1) + (nudge.Y < 0.5f ? 0 : 2)] = true;
        }

        Assert.All(quarters, Assert.True);
    }
}
