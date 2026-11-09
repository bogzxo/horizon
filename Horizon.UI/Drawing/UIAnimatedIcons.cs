using System.Diagnostics;
using System.Numerics;

using Horizon.Core.Tweening;

namespace Horizon.UI.Drawing;

/// <summary>How an icon moves, written after its name with a tilde, <c>$(sync~spin)</c>.</summary>
public enum UIIconMotion : byte
{
    None,

    /// <summary>Turns round, once a second.</summary>
    Spin,

    /// <summary>Swells a little and shrinks back.</summary>
    Pulse,

    /// <summary>Fades down and back up.</summary>
    Breathe,

    /// <summary>Hops up and lands.</summary>
    Bounce,

    /// <summary>Goes for a moment and comes back.</summary>
    Blink,

    /// <summary>Jitters side to side and rests.</summary>
    Shake,

    /// <summary>Beats twice and rests, a heart's.</summary>
    Beat
}

/// <summary>
/// Icons that move. Any icon of <see cref="UISymbols"/> written with a motion after it (<c>$(sync~spin)</c>, a button's
/// icon <c>"bell~shake"</c>) is drawn where its motion has it, and a few aren't in the font at all but drawn,
/// <c>$(spinner)</c> a ring of dots chasing round, <c>$(dots)</c> three dots hopping in turn and <c>$(pulse-dot)</c> a dot
/// that breathes. The motions are tweens (Horizon.Core.Tweening), one looping tween or sequence a motion, played once on a
/// context of their own, each writing a value that every icon of that motion reads as it's drawn. So a hundred spinners
/// are still one tween, everything on screen moves in step, and the context is only ticked while something that moves
/// was drawn a moment ago. With <see cref="UIDrawList.Motion"/> off they're drawn still. Nothing allocates once started.
/// </summary>
public static class UIAnimatedIcons
{
    // The icons that are drawn rather than read out of the font, given characters nothing else is (Unicode's
    // noncharacters), so they go through every place a symbol's character does
    internal const char SPINNER = '﷐', DOTS = '﷑', PULSE_DOT = '﷒';

    /// <summary>How long one round of a motion takes, in seconds.</summary>
    public const float SPIN_TIME = 1.0f, PULSE_TIME = 1.2f, BOUNCE_TIME = 0.9f, BLINK_TIME = 1.4f, SHAKE_TIME = 1.6f, BEAT_TIME = 1.3f, SPINNER_TIME = 0.9f;

    // How long after the last moving icon was drawn the tweens stop being ticked, in seconds
    private const double IDLE_AFTER = 0.5;

    /// <summary>The names of the drawn ones, for a picker of icons.</summary>
    public static IReadOnlyList<string> Drawn { get; } = ["spinner", "dots", "pulse-dot"];

    /// <summary>The names of the motions, as they're written after the tilde.</summary>
    public static IReadOnlyList<string> Motions { get; } = ["spin", "pulse", "breathe", "bounce", "blink", "shake", "beat"];

    // Where each motion is, written by its tween. Spin and spinner go 0 to 1 a round, wave 0 to 1 and back, ring 0 to 1,
    // bounce and the dots up and down, blink 0 or 1, shake swings either side of 0, beat thumps up from 0
    private static float spin, spinner, wave, ring, bounce, blink, shake, beat;
    private static readonly float[] dots = new float[3];

    private static readonly TweenContext tweens = new();
    private static bool started;
    private static long lastTick = long.MinValue, lastUsed;

    /// <summary>
    /// The motions' tweens made and played, once. Each loops for good, the values they write go round and round, and
    /// only move while <see cref="Update"/> ticks them.
    /// </summary>
    private static void Start()
    {
        started = true;
        tweens.Play(Tween.To(() => spin, value => spin = value, 1.0f, SPIN_TIME).SetEasing(Easing.Linear).SetLoops(-1));
        tweens.Play(Tween.To(() => spinner, value => spinner = value, 1.0f, SPINNER_TIME).SetEasing(Easing.Linear).SetLoops(-1));
        tweens.Play(Tween.To(() => wave, value => wave = value, 1.0f, PULSE_TIME * 0.5f).SetEasing(Easing.InOutSine).SetLoops(-1, LoopMode.PingPong));
        tweens.Play(Tween.To(() => ring, value => ring = value, 1.0f, PULSE_TIME).SetEasing(Easing.OutCubic).SetLoops(-1));

        // Up quickly, down hard like something with weight, and a beat on the ground. The dots a beat behind each other
        tweens.Play(Hop(value => bounce = value, () => bounce, 0.0f));
        for (int i = 0; i < dots.Length; i++)
        {
            int dot = i;
            tweens.Play(Hop(value => dots[dot] = value, () => dots[dot], 0.15f * i));
        }

        // Gone for a moment at the end of every round
        tweens.Play(Tween.Sequence()
            .AppendTime(BLINK_TIME * 0.85f)
            .Append(Tween.To(() => blink, value => blink = value, 1.0f, 0.06f).SetEasing(Easing.OutQuad))
            .Append(Tween.To(() => blink, value => blink = value, 0.0f, BLINK_TIME * 0.15f - 0.06f).SetEasing(Easing.InQuad))
            .Build().SetLoops(-1));

        // A few quick swings either way that die down, then still for the rest of the round
        tweens.Play(Tween.Sequence()
            .Append(Swing(1.0f, 0.07f)).Append(Swing(-0.8f, 0.09f)).Append(Swing(0.5f, 0.09f)).Append(Swing(-0.25f, 0.08f)).Append(Swing(0.0f, 0.07f))
            .AppendTime(SHAKE_TIME - 0.4f)
            .Build().SetLoops(-1));

        // Two thumps close together, the second softer, a heart's
        tweens.Play(Tween.Sequence()
            .Append(Thump(1.0f, 0.08f)).Append(Thump(0.0f, 0.1f)).Append(Thump(0.7f, 0.08f)).Append(Thump(0.0f, 0.12f))
            .AppendTime(BEAT_TIME - 0.38f)
            .Build().SetLoops(-1));
    }

    private static Tween Hop(Action<float> set, Func<float> get, float delay) => Tween.Sequence()
        .Append(Tween.To(get, set, 1.0f, BOUNCE_TIME * 0.3f).SetEasing(Easing.OutQuad))
        .Append(Tween.To(get, set, 0.0f, BOUNCE_TIME * 0.35f).SetEasing(Easing.InQuad))
        .AppendTime(BOUNCE_TIME * 0.35f)
        .Build().SetLoops(-1).SetDelay(delay);

    private static Tween Swing(float to, float seconds) =>
        Tween.To(() => shake, value => shake = value, to, seconds).SetEasing(Easing.InOutSine);

    private static Tween Thump(float to, float seconds) =>
        Tween.To(() => beat, value => beat = value, to, seconds).SetEasing(to > 0.0f ? Easing.OutQuad : Easing.InQuad);

    /// <summary>The motions moved on by some time, their tweens made the first time. For a test, or whatever draws without a list.</summary>
    public static void Update(float dt)
    {
        if (!started)
            Start();
        tweens.Tick(dt);
    }

    /// <summary>
    /// The motions moved on by a tick, at the start of every paint of every list, but only while a moving icon was drawn
    /// a moment ago, so a UI that has none ticks nothing. Once a tick however many lists paint in it, two windows
    /// painting don't move them twice as fast. By the real time since the last paint they jittered with whenever the
    /// tick happened to be painted, and the frames between two ticks blended that jitter in.
    /// </summary>
    /// <param name="tick">Which tick of the simulation this is, a list painted outside of one (a test) moves them every time.</param>
    internal static void Advance(float dt, long tick)
    {
        if (tick == lastTick && tick != 0)
            return;

        lastTick = tick;
        if (!started || dt <= 0.0f || (Stopwatch.GetTimestamp() - lastUsed) / (double)Stopwatch.Frequency > IDLE_AFTER)
            return;

        tweens.Tick(MathF.Min(dt, 0.1f));
    }

    /// <summary>Says a moving icon was drawn, which keeps the motions going for a while, made the first time.</summary>
    internal static void Used()
    {
        lastUsed = Stopwatch.GetTimestamp();
        if (!started)
            Start();
    }

    /// <summary>Whether a character is one of the drawn icons.</summary>
    internal static bool IsDrawn(char code) => code is SPINNER or DOTS or PULSE_DOT;

    /// <summary>A drawn icon's character by its name, with either a dash or an underscore in it.</summary>
    internal static bool TryGetDrawn(ReadOnlySpan<char> name, out char code)
    {
        code = name switch
        {
            "spinner" or "loading-dots" => SPINNER,
            "dots" or "typing" => DOTS,
            "pulse-dot" or "pulse_dot" => PULSE_DOT,
            _ => '\0'
        };
        return code != '\0';
    }

    /// <summary>A name split from the motion after its tilde, <c>sync~spin</c> into sync and spin. None when it hasn't one.</summary>
    public static ReadOnlySpan<char> Split(ReadOnlySpan<char> written, out UIIconMotion motion)
    {
        motion = UIIconMotion.None;
        int tilde = written.IndexOf('~');
        if (tilde < 0)
            return written;

        motion = MotionOf(written[(tilde + 1)..]);
        return written[..tilde];
    }

    /// <summary>A motion by its name, None for one there isn't.</summary>
    public static UIIconMotion MotionOf(ReadOnlySpan<char> name) => name switch
    {
        "spin" => UIIconMotion.Spin,
        "pulse" => UIIconMotion.Pulse,
        "breathe" => UIIconMotion.Breathe,
        "bounce" => UIIconMotion.Bounce,
        "blink" => UIIconMotion.Blink,
        "shake" => UIIconMotion.Shake,
        "beat" => UIIconMotion.Beat,
        _ => UIIconMotion.None
    };

    /// <summary>
    /// Where an icon that moves is now, by where its motion's tween has got to. How far round it's turned (radians), how
    /// big it is against its size, how far it's moved (units, Y up) and how much of it shows.
    /// </summary>
    public static (float Turn, float Scale, Vector2 Offset, float Alpha) Now(UIIconMotion motion, float side) => motion switch
    {
        UIIconMotion.Spin => (-MathF.Tau * spin, 1.0f, Vector2.Zero, 1.0f),
        UIIconMotion.Pulse => (0.0f, 1.0f + 0.12f * wave, Vector2.Zero, 1.0f),
        UIIconMotion.Breathe => (0.0f, 1.0f, Vector2.Zero, 1.0f - 0.6f * wave),
        UIIconMotion.Bounce => (0.0f, 1.0f, new Vector2(0.0f, bounce * side * 0.22f), 1.0f),
        UIIconMotion.Blink => (0.0f, 1.0f, Vector2.Zero, 1.0f - 0.85f * blink),
        UIIconMotion.Shake => (shake * 0.25f, 1.0f, new Vector2(shake * side * 0.08f, 0.0f), 1.0f),
        UIIconMotion.Beat => (0.0f, 1.0f + 0.16f * beat, Vector2.Zero, 1.0f),
        _ => (0.0f, 1.0f, Vector2.Zero, 1.0f)
    };

    /// <summary>
    /// Draws one of the drawn icons in the square in the middle of a rectangle, in a colour, where the tweens have it.
    /// Still (the spinner a ring of even dots, the dots level) when <paramref name="moving"/> is false.
    /// </summary>
    internal static void Draw(UIDrawList list, char code, UIRect rect, Vector4 color, bool moving)
    {
        float side = MathF.Min(rect.Width, rect.Height);
        Vector2 middle = rect.Center;
        switch (code)
        {
            case SPINNER:
            {
                // Eight dots round a ring, the one at the head brightest and the ones after it fading
                const int count = 8;
                float radius = side * 0.36f, dot = MathF.Max(1.5f, side * 0.15f);
                float head = moving ? spinner * count : 0.0f;
                for (int i = 0; i < count; i++)
                {
                    float angle = MathF.PI * 0.5f - MathF.Tau * i / count;
                    Vector2 at = middle + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                    float behind = ((head - i) % count + count) % count / count;
                    float alpha = moving ? 1.0f - behind * 0.85f : 0.55f;
                    float grown = moving ? 0.75f + 0.25f * (1.0f - behind) : 0.9f;
                    Dot(list, at, dot * grown, color with { W = color.W * alpha });
                }

                break;
            }

            case DOTS:
            {
                float dot = MathF.Max(1.5f, side * 0.2f), gap = side * 0.3f;
                for (int i = 0; i < 3; i++)
                {
                    float up = moving ? dots[i] : 0.0f;
                    Dot(list, middle + new Vector2((i - 1) * gap, up * side * 0.18f - side * 0.05f), dot, color with { W = color.W * (0.6f + 0.4f * up) });
                }

                break;
            }

            case PULSE_DOT:
            {
                // A dot with a ring going out of it and fading, a light that's on
                float dot = side * 0.42f;
                if (moving)
                    Dot(list, middle, dot * (1.0f + ring * 0.9f), color with { W = color.W * 0.45f * (1.0f - ring) });
                Dot(list, middle, dot, color);
                break;
            }
        }
    }

    // A round dot of a diameter about a point
    private static void Dot(UIDrawList list, Vector2 middle, float diameter, Vector4 color)
    {
        Vector2 half = new(diameter * 0.5f);
        list.RoundRect(new UIRect(middle - half, middle + half), diameter * 0.5f, color);
    }
}
