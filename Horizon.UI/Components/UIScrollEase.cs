namespace Horizon.UI.Components;

/// <summary>
/// Where the wheel wants something scrolled, which it eases to a few pixels a tick rather than jumping there. A jump of
/// three lines in one tick had the frames between two ticks blend the rows that were in sight both times, while the rows
/// that came into sight sat at their new place over the ones still sliding past, so the text flickered as it scrolled.
/// The tree, the table and the scroll panel go by it, the code editor, a list, a dropdown and the completions do the
/// same by hand. What scrolls is painted in a frame of its own (UIDrawList.PushFrame), which a frame between two ticks
/// moves smoothly while the rows in it are the newer tick's.
/// </summary>
internal struct UIScrollEase
{
    /// <summary>How quickly it gets there, a share of the way left every second, most of it in a tenth.</summary>
    public const float RATE = 22.0f;

    /// <summary>Where it's going, null while it's where it's wanted.</summary>
    public float? Goal;

    /// <summary>The give past either end, see <see cref="UIOverscroll"/>, drawn by whoever scrolls with <see cref="UIOverscroll.Shift"/>.</summary>
    public UIOverscroll Over;

    /// <summary>
    /// A turn of the wheel, from where it was going if it hadn't got there yet, so quick turns add up. Whatever of it
    /// would go past an end is thrown into <see cref="Over"/> instead, as hard as the UI's <paramref name="speed"/> lets it.
    /// </summary>
    public void Wheel(float now, float by, float most, float speed = 1.0f)
    {
        most = MathF.Max(0.0f, most);
        float wanted = (Goal ?? now) + by;
        Goal = Math.Clamp(wanted, 0.0f, most);
        if (most > 0.0f)
            Over.Push(wanted - Goal.Value, speed);
    }

    /// <summary>Eases to a place of its own, a row the keys moved to brought into sight.</summary>
    public void To(float goal, float most) => Goal = Math.Clamp(goal, 0.0f, MathF.Max(0.0f, most));

    /// <summary>Somebody else has said where it is (a drag of the bar, a row being shown), the wheel's say is over.</summary>
    public void Stop()
    {
        Goal = null;
        Over.Stop();
    }

    /// <summary>Kept inside of how far it can go, for content that got shorter on the way.</summary>
    public void Clamp(float most)
    {
        if (Goal is { } goal) Goal = Math.Clamp(goal, 0.0f, MathF.Max(0.0f, most));
    }

    /// <summary>A tick's worth of the way there from where it is now, and where that is.</summary>
    public float Step(float now, float dt)
    {
        if (Goal is not { } goal)
            return now;

        now += (goal - now) * (1.0f - MathF.Exp(-RATE * dt));
        if (MathF.Abs(goal - now) < 0.5f)
        {
            Goal = null;
            return goal;
        }

        return now;
    }
}
