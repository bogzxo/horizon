/* GEMINI THE GOAT!!!!!!!!!!!!!!!!! */

using System.Numerics;

using Horizon.Rendering.UIX;

namespace Horizon.Testing;

/// <summary>
/// A scripted pointer for testing a UI without a mouse. Moves, presses and checks are queued up front
/// and played back one after another; the checks are printed as they run, with a tally at the end.
/// Hand <see cref="Pointer"/> to <see cref="UICompositor.PointerSource"/> and call <see cref="Update"/>
/// once per state update.
/// </summary>
internal sealed class UISelfTest
{
    private const float MoveTime = 0.3f;
    private const float PressTime = 0.15f;

    // Glides the pointer to a target with the button in a given state, runs a check, or runs an action.
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

    public UIPointer Pointer => new(position, down);

    /// <summary>Moves to a target and stays there for a while.</summary>
    public void Hover(Func<Vector2> target, float duration = MoveTime)
    {
        Glide(target, false, MoveTime);
        Glide(target, false, duration);
    }

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

    private void Glide(Func<Vector2> target, bool down, float duration) =>
        steps.Enqueue(new Step(target, down, duration));

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

    private void Report()
    {
        if (reported)
            return;

        reported = true;
        Console.WriteLine($"[UI self-test] {passed} of {passed + failed} checks passed.");
    }
}
