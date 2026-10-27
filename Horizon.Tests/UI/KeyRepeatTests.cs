using System.Diagnostics;

using Horizon.UI;

namespace Horizon.Tests;

/// <summary>An arrow held down goes quicker after a while, the way it does in Visual Studio, and nothing else does.</summary>
public class KeyRepeatTests
{
    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void An_arrow_held_long_enough_repeats_quicker_and_a_key_that_edits_never_does()
    {
        float after = UIKeyboard.FastAfter, rate = UIKeyboard.FastRate;
        try
        {
            UIKeyboard.FastAfter = 1.0f;
            UIKeyboard.FastRate = 100.0f;
            long usual = UIKeyboard.IntervalFor(UIKeyboard.RIGHT, Seconds(0.5));

            Assert.Equal(Stopwatch.Frequency / 100, UIKeyboard.IntervalFor(UIKeyboard.RIGHT, Seconds(1.5)));
            Assert.Equal(Stopwatch.Frequency / 100, UIKeyboard.IntervalFor(UIKeyboard.SELECT_DOWN, Seconds(2.0)));
            Assert.True(usual > Stopwatch.Frequency / 100);

            // Backspace held goes on at its own pace, it'd eat a line before anybody let go
            Assert.Equal(usual, UIKeyboard.IntervalFor(UIKeyboard.BACKSPACE, Seconds(5.0)));

            // Off, and a speed slower than the usual one never slows it down
            UIKeyboard.FastAfter = 0.0f;
            Assert.Equal(usual, UIKeyboard.IntervalFor(UIKeyboard.RIGHT, Seconds(5.0)));
            UIKeyboard.FastAfter = 1.0f;
            UIKeyboard.FastRate = 5.0f;
            Assert.Equal(usual, UIKeyboard.IntervalFor(UIKeyboard.RIGHT, Seconds(5.0)));
        }
        finally
        {
            UIKeyboard.FastAfter = after;
            UIKeyboard.FastRate = rate;
        }
    }
}
