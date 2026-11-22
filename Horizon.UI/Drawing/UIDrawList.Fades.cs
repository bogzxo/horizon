using System.Runtime.InteropServices;

namespace Horizon.UI.Drawing;

public sealed partial class UIDrawList
{
    // The edges what's painted fades out towards, see PushFade. Nested, a list inside a list fades by both
    private readonly List<(UIRect Area, float Top, float Bottom)> fades = [];

    // How many of them fade at all. A list at the top of its scroll and too short to scroll has none at either edge,
    // and every quad painted in it worked out a fade of exactly 1 for nothing
    private int fading;

    /// <summary>
    /// Everything painted until the matching <see cref="PopFade"/> fades out as it nears the top or the bottom of an
    /// area, over <paramref name="top"/> and <paramref name="bottom"/> units, each quad by how near the middle of what's
    /// seen of it is. A list scrolled under its edge goes soft there rather than being cut off with a knife, the way an
    /// iPhone's lists go under their bars. Zero for an edge that doesn't fade. It's no clip, push one of those as well.
    /// </summary>
    public void PushFade(UIRect area, float top, float bottom)
    {
        fades.Add((area, MathF.Max(0.0f, top), MathF.Max(0.0f, bottom)));
        if (top > 0.0f || bottom > 0.0f)
            fading++;
    }

    public void PopFade()
    {
        var (_, top, bottom) = fades[^1];
        if (top > 0.0f || bottom > 0.0f)
            fading--;
        fades.RemoveAt(fades.Count - 1);
    }

    // Helper method to start the fades over with the list
    private void BeginFades()
    {
        fades.Clear();
        fading = 0;
    }

    // How much of a quad shows by where the middle of what's seen of it is, all of it out of reach of every fade. Exactly
    // 1 where nothing fades, without working it out
    private float FadeOf(UIRect visible)
    {
        if (fading == 0)
            return 1.0f;

        float shown = 1.0f, y = visible.Center.Y;
        foreach (ref readonly var fade in CollectionsMarshal.AsSpan(fades))
        {
            if (fade.Top > 0.0f)
                shown *= Ease((fade.Area.Max.Y - y) / fade.Top);
            if (fade.Bottom > 0.0f)
                shown *= Ease((y - fade.Area.Min.Y) / fade.Bottom);
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
