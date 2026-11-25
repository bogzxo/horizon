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

    // The longest a tick of the spring is taken to be, see Step
    private const float MAX_STEP = 1.0f / 30.0f;

    /// <summary>How far past the end it's drawn right now, in the scroll's units, past the bottom positive.</summary>
    public float Stretch;

    /// <summary>How fast it's going, units a second, the same way round.</summary>
    public float Velocity;

    /// <summary>Whether it's out past an end or on its way somewhere.</summary>
    public readonly bool Active => Stretch != 0.0f || Velocity != 0.0f;

    // How far a trackpad's fingers have pulled past the end in all, of which Stretch is the band's give. 0 when nothing holds it
    private float pulled;

    // AppKit's and UIKit's rubber band: pulled x past the end of something d long, it gives (1 - 1 / (x c / d + 1)) d
    private const float BAND = 0.55f;

    /// <summary>Whether a trackpad's fingers are holding it out past an end.</summary>
    public readonly bool Held => pulled != 0.0f;

    /// <summary>How far out a pull of <paramref name="pull"/> past the end of something <paramref name="extent"/> long is drawn.</summary>
    internal static float Band(float pull, float extent)
    {
        extent = MathF.Max(1.0f, extent);
        return MathF.Sign(pull) * (1.0f - 1.0f / (MathF.Abs(pull) * BAND / extent + 1.0f)) * extent;
    }

    /// <summary>The pull a stretch of <paramref name="stretch"/> is, what <see cref="Band"/> undoes, for fingers that catch it springing back.</summary>
    internal static float Unband(float stretch, float extent)
    {
        extent = MathF.Max(1.0f, extent);
        float part = MathF.Min(MathF.Abs(stretch) / extent, 0.99f);
        return MathF.Sign(stretch) * extent / BAND * (1.0f / (1.0f - part) - 1.0f);
    }

    /// <summary>
    /// A trackpad's fingers moved by <paramref name="move"/> (the scroll's units, down positive) while it's out past an end:
    /// whatever goes into the stretch is taken, held there with the band's give, and what comes back in through the end is
    /// handed back for the scroll. A stretch springing back is caught where it is. All of it is handed back with nothing out.
    /// </summary>
    public float Hold(float move, float extent)
    {
        if (pulled == 0.0f)
        {
            if (Stretch == 0.0f)
                return move;

            pulled = Unband(Stretch, extent);
        }

        float next = pulled + move;
        if (next == 0.0f || MathF.Sign(next) != MathF.Sign(pulled))
        {
            // Back in through the end, the band is slack and the rest scrolls
            Stop();
            return next;
        }

        pulled = next;
        Stretch = Band(next, extent);
        Velocity = 0.0f;
        GameEngine.Instance?.WindowManager?.RequestSmooth(FRAMES);
        return 0.0f;
    }

    /// <summary>A trackpad's fingers pulled it <paramref name="excess"/> past the end (past the bottom positive), held there with the band's give.</summary>
    public void Pull(float excess, float extent)
    {
        if (excess == 0.0f)
            return;

        if (pulled == 0.0f && Stretch != 0.0f)
            pulled = Unband(Stretch, extent);

        pulled += excess;
        Stretch = Band(pulled, extent);
        Velocity = 0.0f;
        GameEngine.Instance?.WindowManager?.RequestSmooth(FRAMES);
    }

    /// <summary>
    /// The glide after a flick hit the end going <paramref name="velocity"/> units a second (past the bottom positive): it
    /// carries on out and springs back, once. What's left of the glide after that is the end's.
    /// </summary>
    public void Bounce(float velocity, float speed)
    {
        if (Active || speed <= 0.0f || !float.IsFinite(velocity))
            return;

        // Some of the glide's speed goes into the band, enough to show it went out and not so much it flies off
        Velocity = Math.Clamp(velocity * 0.6f, -4000.0f, 4000.0f);
        GameEngine.Instance?.WindowManager?.RequestSmooth(FRAMES);
    }

    /// <summary>
    /// A trackpad's turn moving <paramref name="position"/> (0 to <paramref name="most"/>) by <paramref name="move"/>, down
    /// positive, with the band at the ends the way a Mac's own views have it: held out by the fingers with more give the further
    /// it goes, sprung back once they lift, bounced by the glide after a flick, and thrown a little by a turn that says neither
    /// (a trackpad elsewhere). <paramref name="extent"/> is how long the view is, the band gives less on a short one.
    /// </summary>
    /// <returns>
    /// False for a turn that says no phase (a trackpad that isn't a Mac's) at an end it's going on past, which is for whatever
    /// this is inside of, as a finger's drag is.
    /// </returns>
    public bool Scroll(ref float position, float move, float most, float extent, float speed)
    {
        // Where it is, inside of where it can be. A log scrolled to its end is at float.MaxValue until it's laid out again
        // (ScrollPanel.ScrollToEnd), and a turn up from there was all of that past the end: the band went out its whole way
        // over the newest lines of a chat, and only the next turn scrolled
        most = MathF.Max(0.0f, most);
        if (UIWheel.Tracing)
            UIWheel.Trace(FormattableString.Invariant($"scroll position={position:F1} most={most:F1} move={move:F2} extent={extent:F0} held={UIWheel.Held} gliding={UIWheel.Gliding} stretch={Stretch:F2} velocity={Velocity:F1} pulled={pulled:F2}"));
        position = Math.Clamp(position, 0.0f, most);

        // A stretch past the end it isn't at is the spring's overshoot, nothing to hold: a bounce off the bottom swings a
        // hair past the top for a moment before it settles, and fingers landing on the trackpad then caught that and pulled
        // it out at the top of a chat that was at its bottom, the turn up stretching it and the chat not moving
        if ((Stretch < 0.0f && position > 0.5f) || (Stretch > 0.0f && position < most - 0.5f))
            Stop();

        if (!UIWheel.Held && !UIWheel.Gliding && !Active
            && ((move < 0.0f && position <= 0.0f) || (move > 0.0f && position >= most)))
            return false;

        if (UIWheel.Held)
            move = Hold(move, extent);
        if (move == 0.0f)
            return true;

        float target = position + move;
        position = Math.Clamp(target, 0.0f, MathF.Max(0.0f, most));
        float excess = target - position;
        if (excess == 0.0f || speed <= 0.0f)
            return true;

        if (UIWheel.Held)
            Pull(excess, extent);
        else if (UIWheel.Gliding)
            Bounce(move / MathF.Max(UIWheel.Seconds, 1.0f / 240.0f), speed);
        else
            Push(excess, speed);
        return true;
    }

    /// <summary>The fingers let go of what they held, the spring takes it back from where it is.</summary>
    private void Let() => pulled = 0.0f;

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
        {
            pulled = 0.0f;
            return;
        }

        if (speed <= 0.0f || dt <= 0.0f)
        {
            Stop();
            return;
        }

        // Held by a trackpad's fingers it stays where they have it, and springs back from there once they lift
        if (pulled != 0.0f)
        {
            if (UIWheel.Held)
                return;

            // Let go this tick: the swing starts now, not as long ago as the last tick was (a hold goes quiet)
            Let();
            dt = MathF.Min(dt, 1.0f / 120.0f);
        }

        // A tick at most a frame or two long. The first after a stretch held still is as long as the quiet before it
        // (the idle ticks stretch out), and the whole swing back went by in it: the stretch snapped home
        if (UIWheel.Tracing)
            UIWheel.Trace(FormattableString.Invariant($"spring dt={dt:F4} stretch={Stretch:F2} velocity={Velocity:F1}"));
        dt = MathF.Min(dt, MAX_STEP);

        // A damped spring, ω from how long a swing takes and ζ how much of it is eaten, a few small steps to the tick so
        // a slow frame doesn't fling it off. A finger's pull can be further out than a wheel's throw, and goes back from there
        float omega = MathF.Tau / (RESPONSE / speed);
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / (1.0f / 240.0f)));
        float h = dt / steps;
        float most = MathF.Max(MOST * 1.25f, MathF.Abs(Stretch));
        for (int i = 0; i < steps; i++)
        {
            float pull = -omega * omega * Stretch - 2.0f * DAMPING * omega * Velocity;
            Velocity += pull * h;
            Stretch = Math.Clamp(Stretch + Velocity * h, -most, most);
        }

        if (MathF.Abs(Stretch) < 0.05f && MathF.Abs(Velocity) < 2.0f)
        {
            Stop();
            return;
        }

        GameEngine.Instance?.WindowManager?.RequestSmooth(FRAMES);
    }

    /// <summary>Back at the end at once, somebody else said where the scroll is.</summary>
    public void Stop() => Stretch = Velocity = pulled = 0.0f;

    /// <summary>
    /// How far to move what's scrolled as it's drawn, in the layout's units. A scroll moves its content up as it goes
    /// down, so past the bottom it's moved further up and past the top it comes down off it.
    /// </summary>
    public readonly System.Numerics.Vector2 Shift => new(0.0f, Stretch);

    /// <summary>
    /// Moves what's painted until <see cref="End"/> by the stretch, inside of the clip and around the scroll's own frame,
    /// so the content goes past the end and the clip, the fades and the bar stay put. Keeps an identity frame at rest
    /// too, so the content's frame has the same identity when the spring starts and settles. Always paired with End.
    /// </summary>
    public readonly bool Begin(Drawing.UIDrawList list, UIRect area)
    {
        // Frames are identified by their owner's opening order. Omitting this frame at rest made the scroll frame
        // inherit the spring frame's identity when it settled, and start without a matching previous frame on a push.
        // The rows then snapped instead of interpolating, especially at the bottom of a long file list.

        // A clip moves with what's moved under it, so the one around the scroll came along and the code went out over the
        // tabs. Cut again where the area really is, which inside of the move is the area moved back the other way
        list.PushVisual(Shift, System.Numerics.Vector2.One, area.Center, 1.0f);
        list.PushStationaryClip(area, Shift);
        return true;
    }

    /// <summary>Ends what <see cref="Begin"/> began, when it said it had.</summary>
    public static void End(Drawing.UIDrawList list)
    {
        list.PopClip();
        list.PopVisual();
    }
}
