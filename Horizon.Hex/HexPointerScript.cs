using System.Numerics;

using Horizon.Rendering.UIX;

namespace Horizon.Hex;

/// <summary>
/// A pointer that follows a script rather than the mouse, for the editor to test itself with: moves, presses,
/// things to do and things to check are queued up front and played back one after the other. The checks are
/// printed as they run, with a tally at the end.
/// </summary>
internal sealed class HexPointerScript
{
    private const float MOVE_TIME = 0.16f;
    private const float PRESS_TIME = 0.08f;

    // One of: glide to a target with the button in a given state, wait for something, run something, check something.
    private readonly record struct Step(
        Func<Vector2>? Target = null,
        bool Down = false,
        float Duration = 0.0f,
        string? Name = null,
        Func<bool>? Check = null,
        Action? Action = null,
        Func<bool>? Until = null);

    private readonly Queue<Step> steps = new();
    private Step? current;
    private float elapsed;
    private Vector2 from;

    private Vector2 position;
    private bool down;

    public int Passed { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Whether everything that was queued has played out.</summary>
    public bool IsFinished { get; private set; }

    public UIPointer Pointer => new(position, down);

    /// <summary>Moves to a target and stays there for a while.</summary>
    public void Hover(Func<Vector2> target, float duration = MOVE_TIME)
    {
        Glide(target, false, MOVE_TIME);
        Glide(target, false, duration);
    }

    public void Click(Func<Vector2> target)
    {
        Glide(target, false, MOVE_TIME);
        Glide(target, true, PRESS_TIME);
        Glide(target, false, PRESS_TIME);
    }

    /// <summary>Presses at one place, moves to another with the button held and lets go there.</summary>
    public void Drag(Func<Vector2> start, Func<Vector2> end)
    {
        Glide(start, false, MOVE_TIME);
        Glide(start, true, PRESS_TIME);
        Glide(end, true, MOVE_TIME * 2.0f);
        Glide(end, false, PRESS_TIME);
    }

    /// <summary>Lets some time pass with the pointer where it is, for the UI to catch up with what was done to it.</summary>
    public void Wait(float seconds = 0.12f) =>
        steps.Enqueue(new Step(Duration: seconds));

    /// <summary>Waits for something to be the case, for no longer than a while.</summary>
    public void WaitFor(Func<bool> until, float timeout) =>
        steps.Enqueue(new Step(Duration: timeout, Until: until));

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

        elapsed += dt;

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
}
