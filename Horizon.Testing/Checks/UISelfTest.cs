using System.Numerics;

using Horizon.UI;

namespace Horizon.Testing.Checks;

/// <summary>
/// A fake mouse for testing a UI with nobody at the wheel. You queue up moves, clicks, drags and checks up front
/// and it plays them back one after another, printing every check as it runs and a tally at the end.
/// <code>
/// var test = new UISelfTest();
/// compositor.PointerSource = () => test.Pointer;
/// test.Click(() => button.Module!.ToWorld(button.Bounds.Center));
/// test.Check("the button did its thing", () => clicked);
/// // and then test.Update(dt) in UpdateState
/// </code>
/// Hand <see cref="Pointer"/> to <see cref="UICompositor.PointerSource"/> and call <see cref="Update"/> once per
/// state update. Both happen on the simulation thread, so the checks never race the UI.
/// <para>
/// It goes by updates and not by the clock. A UI reacts to the pointer in the update it is handed it, so a move
/// is two updates, a press is one, and a script of a hundred steps is over in a second or two. It used to glide
/// about at the pace of a hand, a third of a second a move, for nobody. What does take time (a tooltip making up
/// its mind, an entrance playing out, a tween) is waited for by the clock with <see cref="Settle"/>, or better,
/// until it has happened with <see cref="WaitFor"/>. <see cref="Pace"/> slows the lot down for watching.
/// </para>
/// </summary>
internal sealed class UISelfTest
{
    // In updates, at a pace of 1
    private const int MOVE_UPDATES = 2;
    private const int PRESS_UPDATES = 1;
    private const int WAIT_UPDATES = 3;

    // Set this in the environment to a number and every scripted pointer goes that many times slower, 8 is about
    // the pace of a hand
    private const string PACE_VARIABLE = "HORIZON_CHECK_PACE";

    /// <summary>How many times slower than flat out it goes.</summary>
    public int Pace { get; init; } =
        int.TryParse(Environment.GetEnvironmentVariable(PACE_VARIABLE), out int slower) && slower > 0 ? slower : 1;

    // Glides the pointer to a target with the button up or down, lets time or updates pass, runs a check, or runs an action
    private readonly record struct Step(
        Func<Vector2>? Target = null,
        bool Down = false,
        float Duration = 0.0f,
        string? Name = null,
        Func<bool>? Check = null,
        Action? Action = null,
        Func<bool>? Until = null,
        bool Seconds = false);

    private readonly Queue<Step> steps = new();
    private Step? current;
    private float elapsed;
    private Vector2 from;

    private Vector2 position;
    private bool down;

    private int passed;
    private int failed;
    private bool reported;

    /// <summary>Whether the script has run out, every check made.</summary>
    public bool Finished => reported;

    /// <summary>How many checks didn't hold, and how many there were.</summary>
    public int Failed => failed;
    public int Count => passed + failed;

    /// <summary>Where the fake mouse is and whether its button is down, in the UI camera's world space.</summary>
    public UIPointer Pointer => new(position, down);

    /// <summary>Moves to a target and stays there.</summary>
    /// <param name="seconds">How long it stays by the clock, for what only happens to a pointer that keeps still (a tooltip). 0 for only as long as it takes the UI to notice.</param>
    public void Hover(Func<Vector2> target, float seconds = 0.0f)
    {
        Glide(target, false, MOVE_UPDATES);
        Glide(target, false, MOVE_UPDATES);
        if (seconds > 0.0f) Settle(seconds);
    }

    /// <summary>Moves to a target, presses and lets go.</summary>
    public void Click(Func<Vector2> target)
    {
        Glide(target, false, MOVE_UPDATES);
        Glide(target, true, PRESS_UPDATES);
        Glide(target, false, PRESS_UPDATES);
    }

    /// <summary>Presses at one place, moves to another with the button held and lets go there.</summary>
    /// <param name="holdAtStart">How long it holds still before it moves, by the clock, for what tells a press from a drag by that.</param>
    public void Drag(Func<Vector2> start, Func<Vector2> end, float holdAtStart = 0.0f)
    {
        Glide(start, false, MOVE_UPDATES);
        Glide(start, true, PRESS_UPDATES);
        if (holdAtStart > 0.0f) steps.Enqueue(new Step(start, true, holdAtStart, Seconds: true));
        Glide(end, true, MOVE_UPDATES * 2);
        Glide(end, false, PRESS_UPDATES);
    }

    /// <summary>Lets a few updates pass, for the UI to lay out again what was just changed.</summary>
    public void Wait() =>
        steps.Enqueue(new Step(Duration: WAIT_UPDATES * Pace));

    /// <summary>Lets time pass by the clock, for what takes time whatever the pace, an entrance, a tween, a tooltip.</summary>
    public void Settle(float seconds) =>
        steps.Enqueue(new Step(Duration: seconds, Seconds: true));

    /// <summary>Waits for something to be the case, for no longer than a while (in seconds). Better than guessing how long it takes.</summary>
    public void WaitFor(Func<bool> until, float timeout = 2.0f) =>
        steps.Enqueue(new Step(Duration: timeout, Until: until, Seconds: true));

    /// <summary>Queues a condition to test once everything queued before it has played out.</summary>
    public void Check(string name, Func<bool> check) =>
        steps.Enqueue(new Step(Name: name, Check: check));

    /// <summary>Queues something to do once everything queued before it has played out.</summary>
    public void Run(Action action) =>
        steps.Enqueue(new Step(Action: action));

    /// <summary>
    /// Helper method to queue a glide. Targets are functions rather than points because a component's bounds
    /// aren't known until the UI has been laid out, which is well after the script is written. Hand it a point
    /// instead and you'd be clicking on bugger all.
    /// </summary>
    private void Glide(Func<Vector2> target, bool down, int updates) =>
        steps.Enqueue(new Step(target, down, updates * Pace));

    /// <summary>Plays the script on by a state update. Simulation thread, once per update.</summary>
    public void Update(float dt)
    {
        if (current is null)
        {
            if (!steps.TryDequeue(out var next))
            {
                Report();
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
                Console.WriteLine($"[UI self-test] {step.Name} threw: {e.Message}");
                ok = false;
            }

            if (ok) passed++; else failed++;

            Console.WriteLine($"[UI self-test] {(ok ? "pass" : "FAIL")}: {step.Name}");
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

        if (progress >= 1.0f)
            current = null;
    }

    /// <summary>Helper method to print the tally, the once, when the script has run out.</summary>
    private void Report()
    {
        if (reported)
            return;

        reported = true;
        Console.WriteLine($"[UI self-test] {passed} of {passed + failed} checks passed.");
    }
}
