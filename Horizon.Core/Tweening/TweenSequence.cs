namespace Horizon.Core.Tweening;

/// <summary>
/// Puts tweens together into one: <see cref="Append"/> adds a tween after everything so far,
/// <see cref="Join"/> adds one that plays alongside the last one appended.
/// <code>
/// Tween.Sequence()
///     .Append(grow)
///     .Join(fadeIn)
///     .AppendTime(0.5f)
///     .AppendCallback(() => Console.WriteLine("there"))
///     .Append(shrink)
///     .Build();
/// </code>
/// A tween inside of a sequence starts when the sequence gets to it, so a value that two of them move one after
/// the other carries on from where the first one left it. Their own loops and delays are ignored.
/// </summary>
public sealed class TweenSequenceBuilder
{
    private readonly List<(float Start, Tween Tween)> steps = [];

    // Where the last appended tween starts, and where everything so far ends.
    private float lastStart;
    private float end;

    /// <summary>Adds a tween that starts once everything added so far is done.</summary>
    public TweenSequenceBuilder Append(Tween tween)
    {
        lastStart = end;
        steps.Add((lastStart, tween));
        end = lastStart + tween.Duration;
        return this;
    }

    /// <summary>Adds a tween that starts together with the last one that was appended.</summary>
    public TweenSequenceBuilder Join(Tween tween)
    {
        steps.Add((lastStart, tween));
        end = MathF.Max(end, lastStart + tween.Duration);
        return this;
    }

    /// <summary>Adds a gap after everything added so far.</summary>
    public TweenSequenceBuilder AppendTime(float seconds) => Append(Tween.Wait(seconds));

    /// <summary>
    /// Adds a gap that starts together with the last tween that was appended, which keeps the sequence
    /// going for at least that long.
    /// </summary>
    public TweenSequenceBuilder JoinTime(float seconds) => Join(Tween.Wait(seconds));

    /// <summary>Calls something once everything added so far is done.</summary>
    public TweenSequenceBuilder AppendCallback(Action callback) => Append(Tween.Call(callback));

    /// <summary>Calls something when the last tween that was appended starts.</summary>
    public TweenSequenceBuilder JoinCallback(Action callback) => Join(Tween.Call(callback));

    /// <summary>The sequence as one tween, to set up and play like any other.</summary>
    public Tween Build() => new SequenceTween([.. steps], end);
}

internal sealed class SequenceTween((float Start, Tween Tween)[] steps, float duration) : Tween
{
    // Which of the steps have been started and which have been left at their end, in this run.
    private readonly bool[] begun = new bool[steps.Length];
    private readonly bool[] done = new bool[steps.Length];

    public override float Duration { get; } = duration;

    public override Tween SetEasing(Easing easing)
    {
        foreach (var (_, tween) in steps)
            tween.SetEasing(easing);

        return base.SetEasing(easing);
    }

    protected override void Begin() => Restart();

    protected override void Restart()
    {
        Array.Clear(begun);
        Array.Clear(done);
    }

    protected override void Seek(float time)
    {
        // In order, so that of two steps moving the same thing the later one has the last word.
        for (int i = 0; i < steps.Length; i++)
        {
            var (start, tween) = steps[i];
            if (time < start && !begun[i])
                continue;

            if (!begun[i])
            {
                begun[i] = true;
                tween.BeginInternal();
            }

            float local = time - start;

            if (local >= tween.Duration)
            {
                // Left at its end once, and then left alone.
                if (!done[i])
                {
                    done[i] = true;
                    tween.SeekInternal(tween.Duration);
                }
                continue;
            }

            done[i] = false;
            tween.SeekInternal(MathF.Max(0.0f, local));
        }
    }
}
