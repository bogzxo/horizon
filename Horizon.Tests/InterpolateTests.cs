using Horizon.Core.Threading;

namespace Horizon.Tests;

public class InterpolateTests
{
    [Theory]
    [InlineData(350.0f, 10.0f, 0.5f, 360.0f)]
    [InlineData(10.0f, 350.0f, 0.5f, 0.0f)]
    [InlineData(0.0f, 90.0f, 0.5f, 45.0f)]
    [InlineData(-170.0f, 170.0f, 0.5f, -180.0f)]
    public void Angles_go_the_short_way_round(float from, float to, float amount, float expected)
    {
        Assert.Equal(expected, Interpolate.Angle(from, to, amount), 0.001f);
    }

    [Fact]
    public void Packed_colours_are_blended_channel_by_channel()
    {
        uint black = 0xFF000000, white = 0xFFFFFFFF;
        uint grey = Interpolate.PackedColor(black, white, 0.5f);

        Assert.Equal(0xFFu, grey >> 24);
        Assert.Equal(128u, grey & 0xFF);
        Assert.Equal(128u, (grey >> 8) & 0xFF);
        Assert.Equal(128u, (grey >> 16) & 0xFF);
    }

    [Fact]
    public void Steps_are_held_until_all_the_way()
    {
        Assert.Equal(1, Interpolate.Hold(1, 2, 0.99f));
        Assert.Equal(2, Interpolate.Hold(1, 2, 1.0f));
    }
}
