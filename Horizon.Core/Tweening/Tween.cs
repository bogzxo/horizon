using System.Numerics;

namespace Horizon.Core.Tweening;

/// <summary>What a tween that loops does when it gets to its end.</summary>
public enum LoopMode
{
    /// <summary>Starts over from the beginning.</summary>
    Restart,

    /// <summary>Plays backwards to the beginning, then forwards again.</summary>
    PingPong
}

/// <summary>
/// Something that changes over a stretch of time: a value moving to where it should end up, or a
/// <see cref="TweenSequenceBuilder">sequence</see> of those. A tween is made with <see cref="To(Func{float}, Action{float}, float, float, Func{bool}?)"/>
/// and its overloads, set up by chaining (<c>.SetEasing(Easing.OutBack).SetLoops(2).OnComplete(...)</c>) and does
/// nothing until a <see cref="TweenContext"/> plays it, which is also what moves it along every update.
/// Modelled on GTweens.
/// </summary>
public abstract class Tween
{
    private float elapsed;
    private float delayLeft;
    private int loopsDone;
    private bool started;
    private bool backwards;

    private Action? onStart, onComplete, onKill;
    private Action<int>? onLoop;
    private TaskCompletionSource? finished;

    /// <summary>The context that plays this tween, once one has.</summary>
    internal TweenContext? Context { get; set; }

    /// <summary>What the context filed this tween under, see <see cref="TweenContext.Play"/>.</summary>
    internal object? Channel { get; set; }

    /// <summary>How long one run from start to end takes, in seconds.</summary>
    public abstract float Duration { get; }

    public Easing Easing { get; private set; } = Easing.Linear;

    /// <summary>How many times the tween runs, 1 unless told otherwise. Negative for forever.</summary>
    public int Loops { get; private set; } = 1;

    public LoopMode LoopMode { get; private set; }

    /// <summary>How fast time passes for this tween, 2 is twice as fast.</summary>
    public float TimeScale { get; private set; } = 1.0f;

    /// <summary>How long the tween waits before it starts, in seconds.</summary>
    public float Delay { get; private set; }

    public bool IsPlaying { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsKilled { get; private set; }

    /// <summary>Whether the tween got to its end or was killed, either way it is over.</summary>
    public bool IsFinished => IsCompleted || IsKilled;

    /* Making tweens */

    /// <summary>
    /// A tween that moves a value from wherever it is when the tween starts to <paramref name="to"/>.
    /// </summary>
    /// <param name="getter">Reads the value, once, when the tween starts.</param>
    /// <param name="setter">Writes the value, every update.</param>
    /// <param name="validation">Asked before every write. The tween kills itself once it says no, for when whatever is being tweened can go away.</param>
    public static Tween To(Func<float> getter, Action<float> setter, float to, float duration, Func<bool>? validation = null) =>
        new ValueTween<float>(getter, setter, to, duration, static (a, b, t) => a + (b - a) * t, validation);

    /// <inheritdoc cref="To(Func{float}, Action{float}, float, float, Func{bool}?)"/>
    public static Tween To(Func<Vector2> getter, Action<Vector2> setter, Vector2 to, float duration, Func<bool>? validation = null) =>
        new ValueTween<Vector2>(getter, setter, to, duration, static (a, b, t) => a + (b - a) * t, validation);

    /// <inheritdoc cref="To(Func{float}, Action{float}, float, float, Func{bool}?)"/>
    public static Tween To(Func<Vector3> getter, Action<Vector3> setter, Vector3 to, float duration, Func<bool>? validation = null) =>
        new ValueTween<Vector3>(getter, setter, to, duration, static (a, b, t) => a + (b - a) * t, validation);

    /// <inheritdoc cref="To(Func{float}, Action{float}, float, float, Func{bool}?)"/>
    public static Tween To(Func<Vector4> getter, Action<Vector4> setter, Vector4 to, float duration, Func<bool>? validation = null) =>
        new ValueTween<Vector4>(getter, setter, to, duration, static (a, b, t) => a + (b - a) * t, validation);

    /// <summary>A tween that does nothing for a while, for putting a gap into a sequence.</summary>
    public static Tween Wait(float seconds) => new WaitTween(seconds);

    /// <summary>A tween that takes no time and calls something, for doing things at a point in a sequence.</summary>
    public static Tween Call(Action action) => new CallbackTween(action);

    /// <summary>Starts a sequence, tweens that follow each other or play side by side as one tween.</summary>
    public static TweenSequenceBuilder Sequence() => new();

    /* Setting up, all of it chains */

    /// <summary>Sets the curve the tween follows. On a sequence it is given to every tween in it.</summary>
    public virtual Tween SetEasing(Easing easing)
    {
        Easing = easing;
        return this;
    }

    /// <summary>Makes the tween run a number of times, negative for forever.</summary>
    public Tween SetLoops(int loops, LoopMode mode = LoopMode.Restart)
    {
        Loops = loops == 0 ? 1 : loops;
        LoopMode = mode;
        return this;
    }

    public Tween SetTimeScale(float timeScale)
    {
        TimeScale = MathF.Max(0.0f, timeScale);
        return this;
    }

    /// <summary>
    /// Makes the tween wait before it starts. Has no effect on a tween inside of a sequence, which has
    /// <see cref="TweenSequenceBuilder.AppendTime"/> for that.
    /// </summary>
    public Tween SetDelay(float seconds)
    {
        Delay = MathF.Max(0.0f, seconds);
        if (!started)
            delayLeft = Delay;
        return this;
    }

    /// <summary>Called when the tween starts, after its delay.</summary>
    public Tween OnStart(Action callback)
    {
        onStart += callback;
        return this;
    }

    /// <summary>Called when the tween gets to its end. Not called when it is killed.</summary>
    public Tween OnComplete(Action callback)
    {
        onComplete += callback;
        return this;
    }

    /// <summary>Called when the tween is killed before it got to its end.</summary>
    public Tween OnKill(Action callback)
    {
        onKill += callback;
        return this;
    }

    /// <summary>Called every time the tween starts over, with how many runs are behind it.</summary>
    public Tween OnLoop(Action<int> callback)
    {
        onLoop += callback;
        return this;
    }

    /* Controlling */

    /// <summary>
    /// Starts the tween from the beginning, or carries on if it was paused. A tween only moves while a
    /// context ticks it: play a new tween through <see cref="TweenContext.Play"/>, after that this works too.
    /// </summary>
    public Tween Play()
    {
        if (IsFinished || !started)
            Rewind();

        IsPlaying = true;
        Context?.Adopt(this);
        return this;
    }

    /// <summary>Stops the tween where it is, until it is played again.</summary>
    public void Pause() => IsPlaying = false;

    /// <summary>Stops the tween for good, leaving everything where it is right now.</summary>
    public void Kill()
    {
        if (IsFinished)
            return;

        IsPlaying = false;
        IsKilled = true;
        onKill?.Invoke();
        finished?.TrySetResult();
    }

    /// <summary>Jumps to the end: everything is put where the tween would have left it.</summary>
    public void Complete()
    {
        if (IsFinished)
            return;

        if (!started)
            Start();
        if (IsKilled)
            return;

        // A tween that swings back and forth an even number of times ends where it started.
        bool endsAtStart = LoopMode == LoopMode.PingPong && Loops > 0 && Loops % 2 == 0;
        Seek(endsAtStart ? 0.0f : Duration);
        if (!IsKilled)
            Finish();
    }

    /// <summary>Puts the tween back to before it was played, without touching what it has changed so far.</summary>
    public void Reset()
    {
        IsPlaying = false;
        Rewind();
    }

    /// <summary>A task that is done once the tween got to its end or was killed.</summary>
    public Task AwaitCompleteOrKill()
    {
        if (IsFinished)
            return Task.CompletedTask;

        return (finished ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    /// <summary>
    /// Moves the tween along by some time. This is what a <see cref="TweenContext"/> does for the tweens it plays.
    /// </summary>
    public void Tick(float dt)
    {
        if (!IsPlaying)
            return;

        dt *= TimeScale;

        if (delayLeft > 0.0f)
        {
            delayLeft -= dt;
            if (delayLeft > 0.0f)
                return;

            // Whatever is left of this update after the delay ran out belongs to the tween.
            dt = -delayLeft;
        }

        if (!started)
        {
            Start();
            if (IsKilled)
                return;
        }

        elapsed += dt;

        while (elapsed >= Duration)
        {
            Seek(backwards ? 0.0f : Duration);
            if (IsKilled)
                return;

            loopsDone++;
            if (Loops > 0 && loopsDone >= Loops)
            {
                Finish();
                return;
            }

            onLoop?.Invoke(loopsDone);

            if (LoopMode == LoopMode.PingPong)
                backwards = !backwards;
            else
                Restart();

            // A tween that takes no time would loop here forever, one run per update has to do.
            if (Duration <= 0.0f)
            {
                elapsed = 0.0f;
                return;
            }

            elapsed -= Duration;
        }

        Seek(backwards ? Duration - elapsed : elapsed);
    }

    /* For the kinds of tween */

    /// <summary>Called when the tween starts, this is where a value tween looks at where its value is.</summary>
    protected abstract void Begin();

    /// <summary>Called when a looping tween starts over from the beginning.</summary>
    protected virtual void Restart()
    { }

    /// <summary>Puts everything where it belongs at a time between 0 and <see cref="Duration"/>.</summary>
    protected abstract void Seek(float time);

    internal void BeginInternal() => Begin();

    internal void SeekInternal(float time) => Seek(time);

    private void Start()
    {
        started = true;
        Begin();
        if (!IsKilled)
            onStart?.Invoke();
    }

    private void Finish()
    {
        IsPlaying = false;
        IsCompleted = true;
        onComplete?.Invoke();
        finished?.TrySetResult();
    }

    private void Rewind()
    {
        elapsed = 0.0f;
        delayLeft = Delay;
        loopsDone = 0;
        started = false;
        backwards = false;
        IsCompleted = false;
        IsKilled = false;
    }
}

/// <summary>
/// A tween of one value, see <see cref="Tween.To(Func{float}, Action{float}, float, float, Func{bool}?)"/>.
/// </summary>
internal sealed class ValueTween<T>(
    Func<T> getter,
    Action<T> setter,
    T to,
    float duration,
    Func<T, T, float, T> lerp,
    Func<bool>? validation) : Tween
{
    private T from = default!;

    public override float Duration { get; } = MathF.Max(0.0f, duration);

    protected override void Begin()
    {
        if (validation is not null && !validation())
        {
            Kill();
            return;
        }

        from = getter();
    }

    protected override void Seek(float time)
    {
        if (validation is not null && !validation())
        {
            Kill();
            return;
        }

        float t = Duration <= 0.0f ? 1.0f : time / Duration;
        setter(lerp(from, to, Ease.Apply(Easing, t)));
    }
}

internal sealed class WaitTween(float seconds) : Tween
{
    public override float Duration { get; } = MathF.Max(0.0f, seconds);

    protected override void Begin()
    { }

    protected override void Seek(float time)
    { }
}

internal sealed class CallbackTween(Action action) : Tween
{
    public override float Duration => 0.0f;

    protected override void Begin() => action();

    protected override void Seek(float time)
    { }
}
