using Horizon.Engine;

namespace Horizon.UI.Components;

/// <summary>
/// The give at the ends of something that scrolls, iOS's rubber band. The wheel turned on past the top or the bottom
/// throws what's scrolled a little further the way it was going, harder to pull the further out it already is, and a
/// spring brings it back with whatever speed it had, settling with the faintest bounce rather than easing in on a curve.
/// It's only how the content is drawn (see <see cref="Shift"/>), where it is and what the pointer hits stay where the
/// scroll is. As quick as the compositor's AnimationSpeed says, and none of it at 0.
/// </summary>
internal struct UIOverscroll
{
    // How long the spring takes to swing back, in seconds at the usual speed, and how much of its bounce it eats. 1 would
    // settle without going past at all, SwiftUI's smooth spring, a touch under has it land the way a thing with weight does
    private const float RESPONSE = 0.42f, DAMPING = 0.84f;

    // How hard a turn of the wheel past the end throws it, per unit it wanted to go too far, and how far out it can be
    // pulled before it won't give any more
    private const float GAIN = 9.0f, MOST = 110.0f;

    // How long frames keep coming after a tick it moved in, so a Hex that sleeps between keys sees it settle
    private const double FRAMES = 0.12;

    /// <summary>How far past the end it's drawn right now, in the scroll's units, past the bottom positive.</summary>
    public float Stretch;

    /// <summary>How fast it's going, units a second, the same way round.</summary>
    public float Velocity;

    /// <summary>Whether it's out past an end or on its way somewhere.</summary>
    public readonly bool Active => Stretch != 0.0f || Velocity != 0.0f;

    /// <summary>
    /// The scroll wanted to go this much further than it can (negative past the top), a throw past the end with less to it
    /// the further out it is already. Pulling back the other way gets all of it.
    /// </summary>
    public void Push(float excess, float speed)
    {
        if (excess == 0.0f || speed <= 0.0f)
            return;

        float give = 1.0f;
        if (MathF.Sign(excess) == MathF.Sign(Stretch))
        {
            give = 1.0f - MathF.Min(1.0f, MathF.Abs(Stretch) / MOST);
            give *= give;
        }

        Velocity += excess * GAIN * give;
    }

    /// <summary>A tick of the spring, back towards the end it went past.</summary>
    public void Step(float dt, float speed)
    {
        if (!Active)
            return;

        if (speed <= 0.0f || dt <= 0.0f)
        {
            Stop();
            return;
        }

        // A damped spring, ω from how long a swing takes and ζ how much of it is eaten, a few small steps to the tick so
        // a slow frame doesn't fling it off
        float omega = MathF.Tau / (RESPONSE / speed);
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / (1.0f / 240.0f)));
        float h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            float pull = -omega * omega * Stretch - 2.0f * DAMPING * omega * Velocity;
            Velocity += pull * h;
            Stretch = Math.Clamp(Stretch + Velocity * h, -MOST * 1.25f, MOST * 1.25f);
        }

        if (MathF.Abs(Stretch) < 0.05f && MathF.Abs(Velocity) < 2.0f)
        {
            Stop();
            return;
        }

        GameEngine.Instance?.WindowManager?.RequestFrames(FRAMES);
    }

    /// <summary>Back at the end at once, somebody else said where the scroll is.</summary>
    public void Stop() => Stretch = Velocity = 0.0f;

    /// <summary>
    /// How far to move what's scrolled as it's drawn, in the layout's units. A scroll moves its content up as it goes
    /// down, so past the bottom it's moved further up and past the top it comes down off it.
    /// </summary>
    public readonly System.Numerics.Vector2 Shift => new(0.0f, Stretch);

    /// <summary>
    /// Moves what's painted until <see cref="End"/> by the stretch, inside of the clip and around the scroll's own frame,
    /// so the content goes past the end and the clip, the fades and the bar stay put. False when there's no stretch to
    /// draw, and nothing to end.
    /// </summary>
    public readonly bool Begin(Drawing.UIDrawList list, UIRect area)
    {
        if (Stretch == 0.0f)
            return false;

        // A clip moves with what's moved under it, so the one around the scroll came along and the code went out over the
        // tabs. Cut again where the area really is, which inside of the move is the area moved back the other way
        list.PushVisual(Shift, System.Numerics.Vector2.One, area.Center, 1.0f);
        list.PushClip(new UIRect(area.Min - Shift, area.Max - Shift));
        return true;
    }

    /// <summary>Ends what <see cref="Begin"/> began, when it said it had.</summary>
    public static void End(Drawing.UIDrawList list)
    {
        list.PopClip();
        list.PopVisual();
    }
}
