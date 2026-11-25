using System.Diagnostics;

using Horizon.Core;
using Horizon.Platform;

namespace Horizon.Tests;

/// <summary>
/// Covers <see cref="PlatformPower"/> provider registration, event forwarding, <see cref="PlatformPower.SavesEnergy"/>,
/// and Low Power Mode calm/idle pacing in <see cref="WindowManager"/>.
/// </summary>
public class PlatformPowerTests
{
    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void UseForTesting_overrides_and_restores_provider_and_forwards_Changed()
    {
        IPlatformPower original = PlatformPower.Current;
        var fake = new FakePower();
        int notifications = 0;
        Action handler = () => notifications++;

        PlatformPower.Changed += handler;
        try
        {
            using (PlatformPower.UseForTesting(fake))
            {
                Assert.Same(fake, PlatformPower.Current);
                Assert.False(PlatformPower.IsOnBattery);
                Assert.False(PlatformPower.IsLowPowerMode);
                Assert.False(PlatformPower.SavesEnergy);

                fake.Set(onBattery: true, lowPowerMode: false);
                Assert.True(PlatformPower.IsOnBattery);
                Assert.False(PlatformPower.IsLowPowerMode);
                Assert.True(PlatformPower.SavesEnergy);
                Assert.Equal(1, notifications);

                fake.Set(onBattery: true, lowPowerMode: true);
                Assert.True(PlatformPower.IsLowPowerMode);
                Assert.True(PlatformPower.SavesEnergy);
                Assert.Equal(2, notifications);
            }

            Assert.Same(original, PlatformPower.Current);

            // Firing on the detached fake must no longer forward to PlatformPower.Changed
            fake.Set(onBattery: false, lowPowerMode: false);
            Assert.Equal(2, notifications);
        }
        finally
        {
            PlatformPower.Changed -= handler;
        }
    }

    [Fact]
    public void UseForTesting_with_null_uses_noop_AC_provider()
    {
        using (PlatformPower.UseForTesting(null))
        {
            Assert.False(PlatformPower.IsOnBattery);
            Assert.False(PlatformPower.IsLowPowerMode);
            Assert.False(PlatformPower.SavesEnergy);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SavesEnergy_matches_truth_table(bool onBattery, bool lowPowerMode, bool expected)
    {
        var fake = new FakePower(onBattery, lowPowerMode);
        using (PlatformPower.UseForTesting(fake))
        {
            Assert.Equal(onBattery, PlatformPower.IsOnBattery);
            Assert.Equal(lowPowerMode, PlatformPower.IsLowPowerMode);
            Assert.Equal(expected, PlatformPower.SavesEnergy);
        }
    }

    [Fact]
    public void LowPowerMode_scales_calm_pacing_and_idle_threshold_without_throttling_active_input_or_smooth()
    {
        Assert.Equal(WindowManager.IDLE_AFTER, WindowManager.IdleThreshold(lowPowerMode: false), 6);
        Assert.Equal(WindowManager.IDLE_AFTER_LOW_POWER, WindowManager.IdleThreshold(lowPowerMode: true), 6);
        Assert.True(WindowManager.IdleThreshold(lowPowerMode: true) < WindowManager.IdleThreshold(lowPowerMode: false));

        long now = Seconds(10.0);
        long oldInput = now - Seconds(WindowManager.IDLE_AFTER + 1.0);
        long midInput = now - Seconds(0.35); // Past 0.25 s low-power threshold, still inside 0.5 s normal threshold
        long freshInput = now - Seconds(0.10);
        long activeSmooth = now + Seconds(0.05);

        // Normal power mode: 24 Hz focused, 10 Hz unfocused
        Assert.Equal(WindowManager.CALM_PERIOD, WindowManager.CalmPeriodFor(now, oldInput, 0, false, false, false, focused: true, lowPowerMode: false), 6);
        Assert.Equal(WindowManager.CALM_PERIOD_BEHIND, WindowManager.CalmPeriodFor(now, oldInput, 0, false, false, false, focused: false, lowPowerMode: false), 6);

        // Low Power Mode: 12 Hz focused, 4 Hz unfocused
        Assert.Equal(WindowManager.CALM_PERIOD_LOW_POWER, WindowManager.CalmPeriodFor(now, oldInput, 0, false, false, false, focused: true, lowPowerMode: true), 6);
        Assert.Equal(WindowManager.CALM_PERIOD_BEHIND_LOW_POWER, WindowManager.CalmPeriodFor(now, oldInput, 0, false, false, false, focused: false, lowPowerMode: true), 6);

        // Between 0.25 s and 0.5 s after input: normal mode stays at full speed, Low Power Mode already relaxes to calm
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, midInput, 0, false, false, false, focused: true, lowPowerMode: false));
        Assert.Equal(WindowManager.CALM_PERIOD_LOW_POWER, WindowManager.CalmPeriodFor(now, midInput, 0, false, false, false, focused: true, lowPowerMode: true), 6);
        Assert.Equal(WindowManager.CALM_PERIOD_BEHIND_LOW_POWER, WindowManager.CalmPeriodFor(now, midInput, 0, false, false, false, focused: false, lowPowerMode: true), 6);

        // Active typing/scrolling and smooth animations stay 100% unthrottled (0.0) even in Low Power Mode
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, freshInput, 0, false, false, false, focused: true, lowPowerMode: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, activeSmooth, false, false, false, focused: true, lowPowerMode: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, activeSmooth, false, false, false, focused: false, lowPowerMode: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, 0, exclusive: true, lifecycleWork: false, minimized: false, focused: true, lowPowerMode: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, 0, exclusive: false, lifecycleWork: true, minimized: false, focused: true, lowPowerMode: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, 0, exclusive: false, lifecycleWork: false, minimized: true, focused: true, lowPowerMode: true));
    }

    private sealed class FakePower(bool onBattery = false, bool lowPowerMode = false) : IPlatformPower
    {
        public bool IsOnBattery { get; private set; } = onBattery;
        public bool IsLowPowerMode { get; private set; } = lowPowerMode;
        public event Action? Changed;

        public void Set(bool onBattery, bool lowPowerMode)
        {
            IsOnBattery = onBattery;
            IsLowPowerMode = lowPowerMode;
            Changed?.Invoke();
        }
    }
}
