using Horizon.Engine;

namespace Horizon.UI.Drawing;

/// <summary>
/// The pace of what moves only to show something is going on, a spinner, a beating dot: a step at a time, twelve a
/// second (an old Mac's spinner had twelve spokes), not every tick, and not at all while nobody can see the window
/// (behind others or down in the Dock). Every step is a change of what's painted and so a frame drawn and put up, and a
/// spinner going round every tick had the whole UI put together, drawn and presented a hundred and twenty times a second
/// for as long as an agent worked, the GPU never sleeping. Stepped, the frames are as many as the steps, and the ticks
/// between steps paint nothing new.
/// </summary>
internal static class UIDecorative
{
    /// <summary>How long a step is, seconds.</summary>
    public const float STEP = 1.0f / 12.0f;

    /// <summary>Whether it moves now: the window has the keyboard and isn't minimised, nothing moves where nobody's looking.</summary>
    public static bool Runs => GameEngine.Instance?.WindowManager is not { } window || (window.IsFocused && !window.IsMinimized);

    /// <summary>
    /// Time gone by added to what's waiting for the next step, and how much of it to move on by now: a whole step's worth
    /// or more, 0 till then. A tick is asked for when the step is due, so an application gone quiet still steps.
    /// </summary>
    public static float Take(ref float waiting, float dt)
    {
        waiting += MathF.Max(0.0f, dt);
        if (waiting < STEP)
        {
            GameEngine.Instance?.WindowManager?.RequestTickIn(STEP - waiting);
            return 0.0f;
        }

        float moved = waiting;
        waiting = 0.0f;
        GameEngine.Instance?.WindowManager?.RequestTickIn(STEP);
        return moved;
    }
}
