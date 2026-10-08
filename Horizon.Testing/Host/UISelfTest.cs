/* GEMINI THE GOAT!!!!!!!!!!!!!!!!! */

using System.Numerics;

using Horizon.UI;

namespace Horizon.Testing;

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
/// </summary>
internal sealed class UISelfTest
{
    private const float MoveTime = 0.3f;
    private const float PressTime = 0.15f;

    // Glides the pointer to a target with the button up or down, runs a check, or runs an action
    private readonly record struct Step(
        Func<Vector2>? Target = null,
        bool Down = false,
        float Duration = 0.0f,
        string? Name = null,
        Func<bool>? Check = null,
        Action? Action = null);

    private readonly Queue<Step> steps = new();
    private Step? current;
    private float elapsed;
    private Vector2 from;

    private Vector2 position;
    private bool down;

    private int passed;
    private int failed;
    private bool reported;

    /// <summary>Where the fake mouse is and whether its button is down, in the UI camera's world space.</summary>
    public UIPointer Pointer => new(position, down);

    /// <summary>Moves to a target and hangs about there for a while.</summary>
    public void Hover(Func<Vector2> target, float duration = MoveTime)
    {
        Glide(target, false, MoveTime);
        Glide(target, false, duration);
    }

    /// <summary>Moves to a target, presses and lets go.</summary>
    public void Click(Func<Vector2> target)
    {
        Glide(target, false, MoveTime);
        Glide(target, true, PressTime);
        Glide(target, false, PressTime);
    }

    /// <summary>Presses at one place, moves to another with the button held and lets go there.</summary>
    public void Drag(Func<Vector2> start, Func<Vector2> end, float holdAtStart = PressTime)
    {
        Glide(start, false, MoveTime);
        Glide(start, true, holdAtStart);
        Glide(end, true, MoveTime * 2.0f);
        Glide(end, false, PressTime);
    }

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
    private void Glide(Func<Vector2> target, bool down, float duration) =>
        steps.Enqueue(new Step(target, down, duration));

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
            bool ok = step.Check();
            if (ok) passed++; else failed++;

            Console.WriteLine($"[UI self-test] {(ok ? "pass" : "FAIL")}: {step.Name}");
            current = null;
            return;
        }

        elapsed += dt;
        float progress = MathF.Min(1.0f, elapsed / step.Duration);

        position = Vector2.Lerp(from, step.Target!(), progress);
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
