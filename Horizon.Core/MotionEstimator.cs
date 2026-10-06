using System.Numerics;

namespace Horizon.Core;

/// <summary>
/// Works out how fast something is going from where it is every update, in units a second.
/// That sounds like a subtraction, and for something that glides it is. Pixel art doesn't glide: a camera that
/// follows a player is rounded to whole pixels, so it stands still for a few updates and then jumps one. Taken an
/// update at a time that is a speed of nothing, then a burst, then nothing again. This measures every step over the
/// time it took to get there instead, which gives the speed the thing is really going at, and smooths what is left
/// of the unevenness (two pixels this update, three the next).
/// This is what a renderer blurs motion by, see <c>MotionBlurEffect</c>: a blur that knows the real speed hides the
/// stepping, one that goes by single frames would flicker along with it.
/// Updated from one thread and read from another without a lock: a speed that is half a moment old does no harm.
/// </summary>
public sealed class MotionEstimator
{
    // How much of a new measurement is taken over, the rest stays what it was
    private const float SMOOTHING = 0.5f;

    private Vector2 last;
    private Vector2 velocity;
    private float sinceMoved, interval;
    private bool hasLast, isResting = true;

    /// <summary>How fast it is going, in units a second.</summary>
    public Vector2 Velocity => velocity;

    /// <summary>
    /// Further than this in one update is not moving but being put somewhere else (a respawn, a cut), which is no speed at all.
    /// </summary>
    public float TeleportDistance { get; set; } = 256.0f;

    /// <summary>Call once every update with where the thing is now.</summary>
    public void Update(Vector2 position, float dt)
    {
        if (!hasLast || dt <= 0.0f)
        {
            last = position;
            hasLast = true;
            return;
        }

        sinceMoved += dt;
        Vector2 moved = position - last;

        if (moved != Vector2.Zero)
        {
            last = position;

            if (moved.LengthSquared() > TeleportDistance * TeleportDistance)
            {
                Reset();
                return;
            }

            // Over the time it took to get here: a pixel every fourth update is going slowly, not jumping.
            // Something that has only just set off has no such time yet, its first step is measured by the update.
            Vector2 measured = moved / (isResting ? dt : sinceMoved);
            velocity = isResting ? measured : Vector2.Lerp(velocity, measured, SMOOTHING);

            interval = isResting ? dt : sinceMoved;
            sinceMoved = 0.0f;
            isResting = false;
        }
        else if (!isResting && sinceMoved > MathF.Max(interval * 2.0f, dt * 2.5f))
        {
            // It has been still for longer than it ever took between two steps: it has stopped
            Reset();
        }
    }

    /// <summary>Forgets how fast it was going, for when the thing is put somewhere else on purpose.</summary>
    public void Reset()
    {
        velocity = Vector2.Zero;
        sinceMoved = 0.0f;
        interval = 0.0f;
        isResting = true;
    }
}
