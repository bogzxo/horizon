namespace Horizon.UI.Drawing;

public sealed partial class UIDrawList
{
    // The edges what's painted fades out towards, see PushFade. Nested, a list inside a list fades by both
    private readonly List<(UIRect Area, float Top, float Bottom)> fades = [];

    /// <summary>
    /// Everything painted until the matching <see cref="PopFade"/> fades out as it nears the top or the bottom of an
    /// area, over <paramref name="top"/> and <paramref name="bottom"/> units, each quad by how near the middle of what's
    /// seen of it is. A list scrolled under its edge goes soft there rather than being cut off with a knife, the way an
    /// iPhone's lists go under their bars. Zero for an edge that doesn't fade. It's no clip, push one of those as well.
    /// </summary>
    public void PushFade(UIRect area, float top, float bottom) =>
        fades.Add((area, MathF.Max(0.0f, top), MathF.Max(0.0f, bottom)));

    public void PopFade() => fades.RemoveAt(fades.Count - 1);

    // How much of a quad shows by where the middle of what's seen of it is, all of it out of reach of every fade
    private float FadeOf(UIRect visible)
    {
        float shown = 1.0f, y = visible.Center.Y;
        foreach (var (area, top, bottom) in fades)
        {
            if (top > 0.0f)
                shown *= Ease((area.Max.Y - y) / top);
            if (bottom > 0.0f)
                shown *= Ease((y - area.Min.Y) / bottom);
        }

        return shown;
    }

    // Soft at both ends of the fade, a straight ramp showed where it started
    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
