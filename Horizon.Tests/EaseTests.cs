using Horizon.Core.Tweening;

namespace Horizon.Tests;

public class EaseTests
{
    // The whole powers are multiplied out by hand now. This is what they were when MathF.Pow did them
    private static float In(float t, int power) => MathF.Pow(t, power);
    private static float Out(float t, int power) => 1 - MathF.Pow(1 - t, power);
    private static float InOut(float t, int power) =>
        t < 0.5f ? MathF.Pow(2 * t, power) / 2 : 1 - MathF.Pow(-2 * t + 2, power) / 2;

    [Theory]
    [InlineData(Easing.InQuad, Easing.OutQuad, Easing.InOutQuad, 2)]
    [InlineData(Easing.InCubic, Easing.OutCubic, Easing.InOutCubic, 3)]
    [InlineData(Easing.InQuart, Easing.OutQuart, Easing.InOutQuart, 4)]
    [InlineData(Easing.InQuint, Easing.OutQuint, Easing.InOutQuint, 5)]
    public void The_powers_come_out_as_they_did_with_Pow(Easing into, Easing outOf, Easing both, int power)
    {
        for (int i = 0; i <= 200; i++)
        {
            float t = i / 200.0f;

            Assert.Equal(In(t, power), Ease.Apply(into, t), 4);
            Assert.Equal(Out(t, power), Ease.Apply(outOf, t), 4);
            Assert.Equal(InOut(t, power), Ease.Apply(both, t), 4);
        }
    }

    [Fact]
    public void Back_overshoots_the_way_it_did_with_Pow()
    {
        const float back = 1.70158f, backInOut = back * 1.525f;

        for (int i = 0; i <= 200; i++)
        {
            float t = i / 200.0f;

            float outBack = 1 + (back + 1) * MathF.Pow(t - 1, 3) + back * MathF.Pow(t - 1, 2);
            float inOutBack = t < 0.5f
                ? MathF.Pow(2 * t, 2) * ((backInOut + 1) * 2 * t - backInOut) / 2
                : (MathF.Pow(2 * t - 2, 2) * ((backInOut + 1) * (t * 2 - 2) + backInOut) + 2) / 2;

            Assert.Equal(outBack, Ease.Apply(Easing.OutBack, t), 4);
            Assert.Equal(inOutBack, Ease.Apply(Easing.InOutBack, t), 4);
        }
    }

    [Fact]
    public void Every_curve_starts_at_nothing_and_ends_at_all_of_it()
    {
        foreach (Easing easing in Enum.GetValues<Easing>())
        {
            Assert.Equal(0.0f, Ease.Apply(easing, 0.0f), 4);
            Assert.Equal(1.0f, Ease.Apply(easing, 1.0f), 4);

            // And being asked for a time outside of the tween is being asked for one of its ends
            Assert.Equal(0.0f, Ease.Apply(easing, -3.0f), 4);
            Assert.Equal(1.0f, Ease.Apply(easing, 7.0f), 4);
        }
    }
}
