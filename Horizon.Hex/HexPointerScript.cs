using System.Numerics;

using Horizon.UI;

namespace Horizon.Hex;

/// <summary>
/// A pointer that follows a script rather than the mouse, for the editor to test itself with. Moves, presses,
/// things to do and things to check are queued up front and played back one after the other. The checks are
/// printed as they run, with a tally at the end.
/// <para>
/// It goes by updates and not by the clock. A move is a couple of updates, a press is one, and the wait after
/// something was done is the three it takes for the editor to put its lists together again and lay them out,
/// which is all any of it ever needed, the rest was a pointer gliding about for nobody. <see cref="Pace"/> slows it
/// down to something a person can follow. Only what really does take time (a dialog popping up, an entrance
/// playing) is waited for by the clock, with <see cref="Settle"/>.
/// </para>
/// </summary>
internal sealed class HexPointerScript
{
    // In updates, at a pace of 1
    private const int MOVE_UPDATES = 2;
    private const int PRESS_UPDATES = 1;
    private const int WAIT_UPDATES = 3;

    /// <summary>How many times slower than flat out it goes, 1 for as fast as the editor can keep up with, 8 or so to watch.</summary>
    public int Pace { get; init; } = 1;

    private float MOVE_TIME => MOVE_UPDATES * Pace;
    private float PRESS_TIME => PRESS_UPDATES * Pace;

    // One of these. Glide to a target with the button in a given state, wait for something, run something, check something.
    private readonly record struct Step(
        Func<Vector2>? Target = null,
        bool Down = false,
        float Duration = 0.0f,
        string? Name = null,
        Func<bool>? Check = null,
        Action? Action = null,
        Func<bool>? Until = null,
        bool Secondary = false,
        bool Seconds = false);

    private readonly Queue<Step> steps = new();
    private Step? current;
    private float elapsed;
    private Vector2 from;

    private Vector2 position;
    private bool down, secondary;

    public int Passed { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Whether everything that was queued has played out.</summary>
    public bool IsFinished { get; private set; }

    public UIPointer Pointer => new(position, down, secondary);

    /// <summary>Moves to a target and stays there for a while.</summary>
    public void Hover(Func<Vector2> target)
    {
        Glide(target, false, MOVE_TIME);
        Glide(target, false, MOVE_TIME);
    }

    public void Click(Func<Vector2> target)
    {
        Glide(target, false, MOVE_TIME);
        Glide(target, true, PRESS_TIME);
        Glide(target, false, PRESS_TIME);
    }

    /// <summary>Presses at one place, moves to another with the button held and lets go there.</summary>
    /// <summary>A click with the other button, which is what opens a context menu.</summary>
    public void RightClick(Func<Vector2> target)
    {
        Glide(target, false, MOVE_TIME);
        steps.Enqueue(new Step(target, false, PRESS_TIME, Secondary: true));
        Glide(target, false, PRESS_TIME);
    }

    public void Drag(Func<Vector2> start, Func<Vector2> end)
    {
        Glide(start, false, MOVE_TIME);
        Glide(start, true, PRESS_TIME);
        Glide(end, true, MOVE_TIME * 2.0f);
        Glide(end, false, PRESS_TIME);
    }

    /// <summary>
    /// Lets a few updates pass with the pointer where it is, for the UI to catch up with what was done to it (its
    /// lists put together again, and laid out the update after).
    /// </summary>
    public void Wait() =>
        steps.Enqueue(new Step(Duration: WAIT_UPDATES * Pace));

    /// <summary>Lets time pass by the clock, for what takes time whatever the pace is, an entrance, a pop, a tween.</summary>
    public void Settle(float seconds) =>
        steps.Enqueue(new Step(Duration: seconds, Seconds: true));

    /// <summary>Waits for something to be the case, for no longer than a while (in seconds).</summary>
    public void WaitFor(Func<bool> until, float timeout) =>
        steps.Enqueue(new Step(Duration: timeout, Until: until, Seconds: true));

    /// <summary>Queues a condition to test once everything queued before it has played out.</summary>
    public void Check(string name, Func<bool> check) =>
        steps.Enqueue(new Step(Name: name, Check: check));

    /// <summary>Queues something to do once everything queued before it has played out.</summary>
    public void Run(Action action) =>
        steps.Enqueue(new Step(Action: action));

    private void Glide(Func<Vector2> target, bool held, float duration) =>
        steps.Enqueue(new Step(target, held, duration));

    public void Update(float dt)
    {
        if (current is null)
        {
            if (!steps.TryDequeue(out var next))
            {
                if (!IsFinished)
                    Console.WriteLine($"[Hex self-test] {Passed} of {Passed + Failed} checks passed.");

                IsFinished = true;
                return;
            }

            current = next;
            elapsed = 0.0f;
            from = position;
        }

        Step step = current.Value;

        if (step.Action is not null)
        {
            step.Action();
            current = null;
            return;
        }

        if (step.Check is not null)
        {
            bool ok;
            try
            {
                ok = step.Check();
            }
            catch (Exception e)
            {
                // A check that blows up is a check that failed, and the rest still want their turn
                Console.WriteLine($"[Hex self-test] {step.Name} threw: {e.Message}");
                ok = false;
            }

            if (ok) Passed++; else Failed++;

            Console.WriteLine($"[Hex self-test] {(ok ? "pass" : "FAIL")}: {step.Name}");
            current = null;
            return;
        }

        // By the clock for what asked for it, by the update for everything else
        elapsed += step.Seconds ? dt : 1.0f;

        if (step.Target is null)
        {
            // Just time passing, or waiting on something
            if (elapsed >= step.Duration || step.Until?.Invoke() == true)
                current = null;
            return;
        }

        float progress = step.Duration <= 0.0f ? 1.0f : MathF.Min(1.0f, elapsed / step.Duration);

        position = Vector2.Lerp(from, step.Target(), progress);
        down = step.Down;
        secondary = step.Secondary;

        if (progress >= 1.0f)
            current = null;
    }
}
