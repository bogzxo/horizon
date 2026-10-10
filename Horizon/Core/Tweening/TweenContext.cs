namespace Horizon.Core.Tweening;

/// <summary>
/// Plays tweens. It holds the ones that are running and moves them along when it is ticked, once per update.
/// Everything that animates has one (a UI component, a sprite), and a game can make its own for anything else.
/// Tweens can be played from any thread, they are only ever moved along by the one that ticks.
/// </summary>
public sealed class TweenContext
{
    private readonly Lock tweensLock = new();
    private readonly List<Tween> tweens = [];

    // What a tick walks, so that tweens can be played and killed from the callbacks of the ones being ticked.
    private Tween[] ticking = [];

    /// <summary>How many tweens are being held, paused ones included.</summary>
    public int Count
    {
        get
        {
            lock (tweensLock)
                return tweens.Count;
        }
    }

    /// <summary>
    /// Starts a tween and keeps moving it along until it is over.
    /// </summary>
    /// <param name="channel">
    /// What the tween is about, anything that compares equal (a string, an enum). A tween that is still running
    /// on the same channel is killed first, so that two tweens never fight over the same thing.
    /// </param>
    public Tween Play(Tween tween, object? channel = null)
    {
        if (channel is not null)
            Kill(channel);

        tween.Context = this;
        tween.Channel = channel;
        tween.Play();
        return tween;
    }

    /// <summary>Moves every tween along by some time, and lets go of the ones that are over.</summary>
    public void Tick(float dt)
    {
        int count;
        lock (tweensLock)
        {
            count = tweens.Count;
            if (count == 0)
                return;

            if (ticking.Length < count)
                ticking = new Tween[Math.Max(count, ticking.Length * 2)];
            tweens.CopyTo(ticking);
        }

        for (int i = 0; i < count; i++)
            ticking[i].Tick(dt);

        Array.Clear(ticking, 0, count);

        lock (tweensLock)
            tweens.RemoveAll(static tween => tween.IsFinished);
    }

    /// <summary>Kills whatever is running on a channel, see <see cref="Play"/>.</summary>
    public void Kill(object channel)
    {
        // Play asks this every time it is given a channel, and nearly every time there is sod all on it.
        // Looking first saves copying the whole list out just to find that out
        Tween[] snapshot;
        lock (tweensLock)
        {
            bool running = false;
            foreach (var tween in tweens)
            {
                if (tween.IsFinished || !Equals(tween.Channel, channel))
                    continue;

                running = true;
                break;
            }

            if (!running)
                return;

            snapshot = [.. tweens];
        }

        foreach (var tween in snapshot)
        {
            if (Equals(tween.Channel, channel))
                tween.Kill();
        }
    }

    /// <summary>Kills every tween, leaving everything where it is right now.</summary>
    public void KillAll()
    {
        Tween[] snapshot;
        lock (tweensLock)
            snapshot = [.. tweens];

        foreach (var tween in snapshot)
            tween.Kill();
    }

    /// <summary>Jumps every tween to its end.</summary>
    public void CompleteAll()
    {
        Tween[] snapshot;
        lock (tweensLock)
            snapshot = [.. tweens];

        foreach (var tween in snapshot)
            tween.Complete();
    }

    internal void Adopt(Tween tween)
    {
        lock (tweensLock)
        {
            if (!tweens.Contains(tween))
                tweens.Add(tween);
        }
    }
}
