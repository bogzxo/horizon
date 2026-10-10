namespace Horizon.Core.Tweening;

/// <summary>
/// The curves a tween can follow from its start to its end. In starts slowly, Out ends slowly, InOut does both.
/// Back overshoots and comes back, Elastic wobbles, Bounce bounces.
/// </summary>
public enum Easing
{
    Linear,
    InSine, OutSine, InOutSine,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InQuint, OutQuint, InOutQuint,
    InExpo, OutExpo, InOutExpo,
    InCirc, OutCirc, InOutCirc,
    InBack, OutBack, InOutBack,
    InElastic, OutElastic, InOutElastic,
    InBounce, OutBounce, InOutBounce
}

public static class Ease
{
    private const float BACK = 1.70158f;
    private const float BACK_IN_OUT = BACK * 1.525f;

    /// <summary>
    /// How far along a tween is in value once it is <paramref name="t"/> (0 to 1) along in time. Starts at 0
    /// and ends at 1 for every curve, some leave that range in between.
    /// </summary>
    public static float Apply(Easing easing, float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);

        switch (easing)
        {
            case Easing.InSine: return 1 - MathF.Cos(t * MathF.PI / 2);
            case Easing.OutSine: return MathF.Sin(t * MathF.PI / 2);
            case Easing.InOutSine: return -(MathF.Cos(MathF.PI * t) - 1) / 2;

            case Easing.InQuad: return In(t, 2);
            case Easing.OutQuad: return Out(t, 2);
            case Easing.InOutQuad: return InOut(t, 2);
            case Easing.InCubic: return In(t, 3);
            case Easing.OutCubic: return Out(t, 3);
            case Easing.InOutCubic: return InOut(t, 3);
            case Easing.InQuart: return In(t, 4);
            case Easing.OutQuart: return Out(t, 4);
            case Easing.InOutQuart: return InOut(t, 4);
            case Easing.InQuint: return In(t, 5);
            case Easing.OutQuint: return Out(t, 5);
            case Easing.InOutQuint: return InOut(t, 5);

            case Easing.InExpo: return t <= 0 ? 0 : MathF.Pow(2, 10 * t - 10);
            case Easing.OutExpo: return t >= 1 ? 1 : 1 - MathF.Pow(2, -10 * t);
            case Easing.InOutExpo:
                if (t <= 0) return 0;
                if (t >= 1) return 1;
                return t < 0.5f ? MathF.Pow(2, 20 * t - 10) / 2 : (2 - MathF.Pow(2, -20 * t + 10)) / 2;

            case Easing.InCirc: return 1 - MathF.Sqrt(1 - t * t);
            case Easing.OutCirc: return MathF.Sqrt(1 - (t - 1) * (t - 1));
            case Easing.InOutCirc:
                return t < 0.5f
                    ? (1 - MathF.Sqrt(1 - 4 * t * t)) / 2
                    : (MathF.Sqrt(1 - (-2 * t + 2) * (-2 * t + 2)) + 1) / 2;

            case Easing.InBack: return (BACK + 1) * t * t * t - BACK * t * t;
            case Easing.OutBack: return 1 + (BACK + 1) * MathF.Pow(t - 1, 3) + BACK * MathF.Pow(t - 1, 2);
            case Easing.InOutBack:
                return t < 0.5f
                    ? MathF.Pow(2 * t, 2) * ((BACK_IN_OUT + 1) * 2 * t - BACK_IN_OUT) / 2
                    : (MathF.Pow(2 * t - 2, 2) * ((BACK_IN_OUT + 1) * (t * 2 - 2) + BACK_IN_OUT) + 2) / 2;

            case Easing.InElastic: return 1 - OutElastic(1 - t);
            case Easing.OutElastic: return OutElastic(t);
            case Easing.InOutElastic:
                return t < 0.5f ? (1 - OutElastic(1 - 2 * t)) / 2 : (1 + OutElastic(2 * t - 1)) / 2;

            case Easing.InBounce: return 1 - OutBounce(1 - t);
            case Easing.OutBounce: return OutBounce(t);
            case Easing.InOutBounce:
                return t < 0.5f ? (1 - OutBounce(1 - 2 * t)) / 2 : (1 + OutBounce(2 * t - 1)) / 2;

            default: return t;
        }
    }

    // A whole power is multiplying a few times. MathF.Pow gets there through logarithms, the scenic route,

    private static float Out(float t, int power) => 1 - MathF.Pow(1 - t, power);

    private static float InOut(float t, int power) =>
        t < 0.5f ? MathF.Pow(2 * t, power) / 2 : 1 - MathF.Pow(-2 * t + 2, power) / 2;

    private static float OutElastic(float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return MathF.Pow(2, -10 * t) * MathF.Sin((t * 10 - 0.75f) * (2 * MathF.PI / 3)) + 1;
    }

    private static float OutBounce(float t)
    {
        const float n = 7.5625f, d = 2.75f;

        if (t < 1 / d) return n * t * t;
        if (t < 2 / d) return n * (t -= 1.5f / d) * t + 0.75f;
        if (t < 2.5f / d) return n * (t -= 2.25f / d) * t + 0.9375f;
        return n * (t -= 2.625f / d) * t + 0.984375f;
    }
}
