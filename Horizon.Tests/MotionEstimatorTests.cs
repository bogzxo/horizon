using System.Numerics;

using Horizon.Core;

namespace Horizon.Tests;

public class MotionEstimatorTests
{
    [Fact]
    public void A_steady_glide_is_measured_at_its_speed()
    {
        var motion = new MotionEstimator();
        Vector2 position = Vector2.Zero;

        for (int i = 0; i < 20; i++)
        {
            position += new Vector2(3.0f, 0.0f);
            motion.Update(position, 1.0f / 120.0f);
        }

        Assert.Equal(360.0f, motion.Velocity.X, 0.01f);
        Assert.Equal(0.0f, motion.Velocity.Y, 0.01f);
    }

    [Fact]
    public void A_teleport_is_no_speed_at_all()
    {
        var motion = new MotionEstimator();
        motion.Update(Vector2.Zero, 1.0f / 120.0f);
        motion.Update(new Vector2(1000.0f, 0.0f), 1.0f / 120.0f);

        Assert.Equal(Vector2.Zero, motion.Velocity);
    }
}
