namespace Horizon.UI.Components;

/// <summary>
/// Where the wheel wants something scrolled, which it eases to a few pixels a tick rather than jumping there. A jump of
/// three lines in one tick had the frames between two ticks blend the rows that were in sight both times, while the rows
/// that came into sight sat at their new place over the ones still sliding past, so the text flickered as it scrolled.
/// The tree, the table and the scroll panel go by it, the code editor, a list, a dropdown and the completions do the
/// same by hand. While it eases a component paints its rows in steps (UIDrawList.BeginSteps), so a frame between two
/// ticks has them where the newer tick put them and a row coming into sight never sits over one sliding out.
/// </summary>
internal struct UIScrollEase
{
    /// <summary>How quickly it gets there, a share of the way left every second, most of it in a tenth.</summary>
    public const float RATE = 22.0f;

    /// <summary>Where it's going, null while it's where it's wanted.</summary>
    public float? Goal;

    /// <summary>A turn of the wheel, from where it was going if it hadn't got there yet, so quick turns add up.</summary>
    public void Wheel(float now, float by, float most) => Goal = Math.Clamp((Goal ?? now) + by, 0.0f, MathF.Max(0.0f, most));

    /// <summary>Eases to a place of its own, a row the keys moved to brought into sight.</summary>
    public void To(float goal, float most) => Goal = Math.Clamp(goal, 0.0f, MathF.Max(0.0f, most));

    /// <summary>Somebody else has said where it is (a drag of the bar, a row being shown), the wheel's say is over.</summary>
    public void Stop() => Goal = null;

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
