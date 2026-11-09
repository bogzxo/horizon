using Horizon.Core.Threading;

namespace Horizon.Tests;

public class LoopStatisticsTests
{
    [Fact]
    public void Allocation_intervals_exclude_startup_and_include_every_catchup_turn()
    {
        var statistics = new LoopStatistics("test", 120);
        statistics.Record(1, 0, 1, allocatedBytes: 1_000_000);
        var start = statistics.AllocationTotals;
        statistics.Record(0.01, 0, 0.01, turns: 3, allocatedBytes: 96);
        statistics.Record(0.01, 0, 0.01, allocatedBytes: 0);
        var end = statistics.AllocationTotals;
        Assert.Equal(4, end.Turns - start.Turns);
        Assert.Equal(96, end.Bytes - start.Bytes);
        Assert.True(statistics.AllocatedPerTurn > 96); // The smoothed display still includes startup.
    }

    // The peak is kept as the turns come and go instead of being looked for every time. However the turns fall it
    // has to be what going over the whole history would have found
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(2024)]
    public void The_peak_is_the_longest_turn_still_in_the_history(int seed)
    {
        var statistics = new LoopStatistics("test", 60.0);
        var random = new Random(seed);
        Span<float> history = stackalloc float[LoopStatistics.HISTORY];

        for (int turn = 0; turn < LoopStatistics.HISTORY * 5; turn++)
        {
            // Mostly quick turns with a long one now and then, which is the one that has to be forgotten again
            // once it is 240 turns old
            double work = random.Next(40) == 0 ? 0.004 + random.NextDouble() * 0.02 : random.NextDouble() * 0.002;
            statistics.Record(work, 0.0, 1.0 / 60.0);

            statistics.CopyHistory(history);
            float longest = 0.0f;
            foreach (float value in history) longest = MathF.Max(longest, value);

            Assert.Equal(longest, (float)statistics.PeakWorkMs);
        }
    }

    [Fact]
    public void A_long_turn_is_forgotten_once_it_has_left_the_history()
    {
        var statistics = new LoopStatistics("test", 60.0);

        statistics.Record(0.050, 0.0, 1.0 / 60.0);
        Assert.Equal(50.0, statistics.PeakWorkMs, 3);

        for (int turn = 0; turn < LoopStatistics.HISTORY - 1; turn++)
            statistics.Record(0.001, 0.0, 1.0 / 60.0);
        Assert.Equal(50.0, statistics.PeakWorkMs, 3);

        statistics.Record(0.001, 0.0, 1.0 / 60.0);
        Assert.Equal(1.0, statistics.PeakWorkMs, 3);
    }
}
