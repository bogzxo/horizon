using System.Numerics;

namespace Horizon.Core.Tweening;

/// <summary>
/// Value-type channel identifier for <see cref="TweenContext"/>. Avoids boxing enum or integer channels while
/// staying compatible with string/object tags.
/// </summary>
public readonly record struct TweenChannel(int Key, object? Tag)
{
    public bool IsNone => Key == 0 && Tag is null;

    public TweenChannel(int key) : this(key, null)
    { }

    public static implicit operator TweenChannel(int key) => new(key, null);

    public static implicit operator TweenChannel(string? tag) => new(0, tag);
}

/// <summary>
/// Zero-allocation handle to a struct-backed tween running inside a <see cref="TweenContext"/>.
/// Exposes the same fluent setup and control API as <see cref="Tween"/>.
/// </summary>
public readonly struct TweenHandle
{
    private readonly TweenContext? context;
    private readonly int slot;
    private readonly int generation;

    internal TweenHandle(TweenContext context, int slot, int generation)
    {
        this.context = context;
        this.slot = slot;
        this.generation = generation;
    }

    public bool IsValid => context is not null && generation != 0;

    public float Duration => context?.GetSlotDuration(slot, generation) ?? 0.0f;
    public Easing Easing => context?.GetSlotEasing(slot, generation) ?? Easing.Linear;
    public int Loops => context?.GetSlotLoops(slot, generation) ?? 1;
    public LoopMode LoopMode => context?.GetSlotLoopMode(slot, generation) ?? LoopMode.Restart;
    public float TimeScale => context?.GetSlotTimeScale(slot, generation) ?? 1.0f;
    public float Delay => context?.GetSlotDelay(slot, generation) ?? 0.0f;

    public bool IsPlaying => context?.GetSlotIsPlaying(slot, generation) ?? false;
    public bool IsCompleted => context?.GetSlotIsCompleted(slot, generation) ?? false;
    public bool IsKilled => context?.GetSlotIsKilled(slot, generation) ?? false;
    public bool IsFinished => context is null || context.GetSlotIsFinished(slot, generation);

    public TweenHandle SetEasing(Easing easing)
    {
        context?.SetSlotEasing(slot, generation, easing);
        return this;
    }

    public TweenHandle SetLoops(int loops, LoopMode mode = LoopMode.Restart)
    {
        context?.SetSlotLoops(slot, generation, loops, mode);
        return this;
    }

    public TweenHandle SetTimeScale(float timeScale)
    {
        context?.SetSlotTimeScale(slot, generation, timeScale);
        return this;
    }

    public TweenHandle SetDelay(float seconds)
    {
        context?.SetSlotDelay(slot, generation, seconds);
        return this;
    }

    public TweenHandle OnStart(Action callback)
    {
        context?.AddSlotOnStart(slot, generation, callback);
        return this;
    }

    public TweenHandle OnComplete(Action callback)
    {
        context?.AddSlotOnComplete(slot, generation, callback);
        return this;
    }

    public TweenHandle OnKill(Action callback)
    {
        context?.AddSlotOnKill(slot, generation, callback);
        return this;
    }

    public TweenHandle OnLoop(Action<int> callback)
    {
        context?.AddSlotOnLoop(slot, generation, callback);
        return this;
    }

    public TweenHandle Play()
    {
        context?.PlaySlot(slot, generation);
        return this;
    }

    public void Pause() => context?.PauseSlot(slot, generation);

    public void Kill() => context?.KillSlot(slot, generation);

    public void Complete() => context?.CompleteSlot(slot, generation);

    public void Reset() => context?.ResetSlot(slot, generation);

    public Task AwaitCompleteOrKill() => context?.AwaitSlot(slot, generation) ?? Task.CompletedTask;
}

/// <summary>
/// Plays tweens. It holds the ones that are running and moves them along when it is ticked, once per update.
/// Everything that animates has one (a UI component, a sprite), and a game can make its own for anything else.
/// Tweens can be played from any thread, they are only ever moved along by the one that ticks.
/// </summary>
public sealed class TweenContext
{
    private enum SlotKind : byte
    {
        Empty = 0,
        Float,
        Vector2,
        Vector3,
        Vector4,
        TwoStepVector2,
        ShakeVector2
    }

    private struct TweenSlot
    {
        public SlotKind Kind;
        public int Generation;
        public TweenChannel Channel;

        public object? Target;
        public Delegate? Getter;
        public Delegate? Setter;

        // Value endpoints: Float in X, Vector2 in XY, Vector3 in XYZ, Vector4 in XYZW.
        // For ShakeVector2: From.X/Y = home, From.Z = strength.
        public Vector4 From;
        public Vector4 To;
        public Vector4 To2;

        public float Duration;
        public float Duration2;
        public Easing Easing;
        public Easing Easing2;

        public float Elapsed;
        public float Delay;
        public float DelayLeft;
        public float TimeScale;
        public int Loops;
        public int LoopsDone;
        public LoopMode LoopMode;

        public bool Started;
        public bool Backwards;
        public bool IsPlaying;
        public bool IsCompleted;
        public bool IsKilled;
        public bool Step2Begun;

        public Action? OnStart;
        public Action? OnComplete;
        public Action? OnKill;
        public Action<int>? OnLoop;
        public TaskCompletionSource? Finished;

        public readonly bool IsFinished => IsCompleted || IsKilled;
        public readonly float TotalDuration => Kind == SlotKind.TwoStepVector2 ? Duration + Duration2 : Duration;
    }

    private readonly Lock tweensLock = new();
    private readonly List<Tween> tweens = [];

    // Struct-backed zero-allocation tween slots
    private TweenSlot[]? slots;
    private int activeSlotCount;
    private int nextGeneration = 1;

    // Reusable tick snapshots so callbacks can play or kill tweens safely
    private Tween[] ticking = [];
    private int[] tickingSlotIndices = [];
    private int[] tickingSlotGenerations = [];

    // Reusable scratch buffer for Kill(channel) when more than one tween matches the same channel
    private Tween[]? killScratch;
    private Action?[]? killCallbackScratch;
    private TaskCompletionSource?[]? killTcsScratch;

    /// <summary>How many tweens are being held, paused ones included.</summary>
    public int Count
    {
        get
        {
            lock (tweensLock)
                return tweens.Count + activeSlotCount;
        }
    }

    /// <summary>
    /// Starts a tween and keeps moving it along until it is over.
    /// </summary>
    /// <param name="channel">
    /// What the tween is about. A tween that is still running on the same channel is killed first,
    /// so that two tweens never fight over the same thing.
    /// </param>
    public Tween Play(Tween tween, TweenChannel channel = default)
    {
        if (!channel.IsNone)
            Kill(channel);

        tween.Context = this;
        tween.Channel = channel;
        tween.Play();
        return tween;
    }

    /// <inheritdoc cref="Play(Tween, TweenChannel)"/>
    public Tween Play(Tween tween, object? channel) =>
        Play(tween, channel is null ? default : new TweenChannel(0, channel));

    /// <summary>
    /// Starts a zero-allocation float tween using cached static getter/setter delegates.
    /// </summary>
    public TweenHandle PlayFloat(
        object target,
        Func<object, float> getter,
        Action<object, float> setter,
        float to,
        float duration,
        TweenChannel channel = default,
        Easing easing = Easing.Linear)
    {
        if (!channel.IsNone)
            Kill(channel);

        lock (tweensLock)
        {
            int idx = AllocateSlotLocked(out int gen);
            ref TweenSlot s = ref slots![idx];
            s.Kind = SlotKind.Float;
            s.Channel = channel;
            s.Target = target;
            s.Getter = getter;
            s.Setter = setter;
            s.To = new Vector4(to, 0.0f, 0.0f, 0.0f);
            s.Duration = MathF.Max(0.0f, duration);
            s.Easing = easing;
            s.TimeScale = 1.0f;
            s.Loops = 1;
            s.IsPlaying = true;
            activeSlotCount++;
            return new TweenHandle(this, idx, gen);
        }
    }

    /// <summary>
    /// Starts a zero-allocation <see cref="Vector2"/> tween using cached static getter/setter delegates.
    /// </summary>
    public TweenHandle PlayVector2(
        object target,
        Func<object, Vector2> getter,
        Action<object, Vector2> setter,
        Vector2 to,
        float duration,
        TweenChannel channel = default,
        Easing easing = Easing.Linear)
    {
        if (!channel.IsNone)
            Kill(channel);

        lock (tweensLock)
        {
            int idx = AllocateSlotLocked(out int gen);
            ref TweenSlot s = ref slots![idx];
            s.Kind = SlotKind.Vector2;
            s.Channel = channel;
            s.Target = target;
            s.Getter = getter;
            s.Setter = setter;
            s.To = new Vector4(to.X, to.Y, 0.0f, 0.0f);
            s.Duration = MathF.Max(0.0f, duration);
            s.Easing = easing;
            s.TimeScale = 1.0f;
            s.Loops = 1;
            s.IsPlaying = true;
            activeSlotCount++;
            return new TweenHandle(this, idx, gen);
        }
    }

    /// <summary>
    /// Starts a zero-allocation <see cref="Vector3"/> tween using cached static getter/setter delegates.
    /// </summary>
    public TweenHandle PlayVector3(
        object target,
        Func<object, Vector3> getter,
        Action<object, Vector3> setter,
        Vector3 to,
        float duration,
        TweenChannel channel = default,
        Easing easing = Easing.Linear)
    {
        if (!channel.IsNone)
            Kill(channel);

        lock (tweensLock)
        {
            int idx = AllocateSlotLocked(out int gen);
            ref TweenSlot s = ref slots![idx];
            s.Kind = SlotKind.Vector3;
            s.Channel = channel;
            s.Target = target;
            s.Getter = getter;
            s.Setter = setter;
            s.To = new Vector4(to.X, to.Y, to.Z, 0.0f);
            s.Duration = MathF.Max(0.0f, duration);
            s.Easing = easing;
            s.TimeScale = 1.0f;
            s.Loops = 1;
            s.IsPlaying = true;
            activeSlotCount++;
            return new TweenHandle(this, idx, gen);
        }
    }

    /// <summary>
    /// Starts a zero-allocation <see cref="Vector4"/> tween using cached static getter/setter delegates.
    /// </summary>
    public TweenHandle PlayVector4(
        object target,
        Func<object, Vector4> getter,
        Action<object, Vector4> setter,
        Vector4 to,
        float duration,
        TweenChannel channel = default,
        Easing easing = Easing.Linear)
    {
        if (!channel.IsNone)
            Kill(channel);

        lock (tweensLock)
        {
            int idx = AllocateSlotLocked(out int gen);
            ref TweenSlot s = ref slots![idx];
            s.Kind = SlotKind.Vector4;
            s.Channel = channel;
            s.Target = target;
            s.Getter = getter;
            s.Setter = setter;
            s.To = to;
            s.Duration = MathF.Max(0.0f, duration);
            s.Easing = easing;
            s.TimeScale = 1.0f;
            s.Loops = 1;
            s.IsPlaying = true;
            activeSlotCount++;
            return new TweenHandle(this, idx, gen);
        }
    }

    /// <summary>
    /// Starts a zero-allocation two-step <see cref="Vector2"/> sequence (such as a punch) without allocating a
    /// <see cref="TweenSequenceBuilder"/> or <see cref="SequenceTween"/>.
    /// </summary>
    public TweenHandle PlayTwoStepVector2(
        object target,
        Func<object, Vector2> getter,
        Action<object, Vector2> setter,
        Vector2 to1,
        float duration1,
        Easing easing1,
        Vector2 to2,
        float duration2,
        Easing easing2,
        TweenChannel channel = default)
    {
        if (!channel.IsNone)
            Kill(channel);

        lock (tweensLock)
        {
            int idx = AllocateSlotLocked(out int gen);
            ref TweenSlot s = ref slots![idx];
            s.Kind = SlotKind.TwoStepVector2;
            s.Channel = channel;
            s.Target = target;
            s.Getter = getter;
            s.Setter = setter;
            s.To = new Vector4(to1.X, to1.Y, 0.0f, 0.0f);
            s.To2 = new Vector4(to2.X, to2.Y, 0.0f, 0.0f);
            s.Duration = MathF.Max(0.0f, duration1);
            s.Duration2 = MathF.Max(0.0f, duration2);
            s.Easing = easing1;
            s.Easing2 = easing2;
            s.TimeScale = 1.0f;
            s.Loops = 1;
            s.IsPlaying = true;
            activeSlotCount++;
            return new TweenHandle(this, idx, gen);
        }
    }

    /// <summary>
    /// Starts a zero-allocation dying shake around <paramref name="home"/> on a <see cref="Vector2"/> property.
    /// </summary>
    public TweenHandle PlayShakeVector2(
        object target,
        Action<object, Vector2> setter,
        Vector2 home,
        float strength,
        float duration,
        TweenChannel channel = default)
    {
        if (!channel.IsNone)
            Kill(channel);

        lock (tweensLock)
        {
            int idx = AllocateSlotLocked(out int gen);
            ref TweenSlot s = ref slots![idx];
            s.Kind = SlotKind.ShakeVector2;
            s.Channel = channel;
            s.Target = target;
            s.Setter = setter;
            s.From = new Vector4(home.X, home.Y, strength, 0.0f);
            s.Duration = MathF.Max(0.0f, duration);
            s.Easing = Easing.Linear;
            s.TimeScale = 1.0f;
            s.Loops = 1;
            s.IsPlaying = true;
            activeSlotCount++;
            return new TweenHandle(this, idx, gen);
        }
    }

    /// <summary>
    /// Whether the last tick moved a tween that comes to an end, something on its way somewhere (a slide, a pop), not one
    /// that goes on for ever (a pulse). For an application that only gives the full rate to what's on its way, see
    /// WindowManager.RequestSmooth.
    /// </summary>
    public bool MovedFinite { get; private set; }

    /// <summary>Moves every tween along by some time, and lets go of the ones that are over.</summary>
    public void Tick(float dt)
    {
        MovedFinite = false;
        int tweenCount;
        int slotTickCount = 0;

        lock (tweensLock)
        {
            tweenCount = tweens.Count;
            if (tweenCount > 0)
            {
                if (ticking.Length < tweenCount)
                    ticking = new Tween[Math.Max(tweenCount, ticking.Length * 2)];
                tweens.CopyTo(ticking);
            }

            if (activeSlotCount > 0 && slots is { } arr)
            {
                if (tickingSlotIndices.Length < activeSlotCount)
                {
                    int cap = Math.Max(activeSlotCount, Math.Max(tickingSlotIndices.Length * 2, 4));
                    tickingSlotIndices = new int[cap];
                    tickingSlotGenerations = new int[cap];
                }

                for (int i = 0; i < arr.Length; i++)
                {
                    ref readonly TweenSlot s = ref arr[i];
                    if (s.Kind == SlotKind.Empty || s.IsFinished || !s.IsPlaying)
                        continue;

                    if (slotTickCount == tickingSlotIndices.Length)
                    {
                        int cap = tickingSlotIndices.Length * 2;
                        Array.Resize(ref tickingSlotIndices, cap);
                        Array.Resize(ref tickingSlotGenerations, cap);
                    }

                    tickingSlotIndices[slotTickCount] = i;
                    tickingSlotGenerations[slotTickCount] = s.Generation;
                    slotTickCount++;
                }
            }
        }

        if (tweenCount == 0 && slotTickCount == 0)
            return;

        for (int i = 0; i < tweenCount; i++)
        {
            if (ticking[i].Loops > 0 && !ticking[i].IsFinished)
                MovedFinite = true;
            ticking[i].Tick(dt);
        }

        if (tweenCount > 0)
        {
            Array.Clear(ticking, 0, tweenCount);

            lock (tweensLock)
                tweens.RemoveAll(static tween => tween.IsFinished);
        }

        for (int i = 0; i < slotTickCount; i++)
        {
            TickSlot(tickingSlotIndices[i], tickingSlotGenerations[i], dt);
        }
    }

    /// <summary>Kills whatever is running on a channel, see <see cref="Play(Tween, TweenChannel)"/>.</summary>
    public void Kill(TweenChannel channel)
    {
        if (channel.IsNone)
            return;

        // Play asks this every time it is given a channel, and nearly every time there is either nothing or one tween
        // on it. Keeping the single-match case in locals avoids touching any array, and multiple matches reuse scratch storage.
        Tween? single = null;
        Tween[]? scratch = null;
        int count = 0;

        Action? singleSlotKill = null;
        TaskCompletionSource? singleSlotTcs = null;
        Action?[]? slotKillScratch = null;
        TaskCompletionSource?[]? slotTcsScratch = null;
        int slotKillCount = 0;

        lock (tweensLock)
        {
            for (int i = 0; i < tweens.Count; i++)
            {
                Tween tween = tweens[i];
                if (tween.IsFinished || tween.Channel != channel)
                    continue;

                if (count == 0)
                {
                    single = tween;
                    count = 1;
                }
                else
                {
                    if (scratch is null)
                    {
                        scratch = killScratch;
                        killScratch = null;
                        if (scratch is null || scratch.Length < 4)
                            scratch = new Tween[4];
                        scratch[0] = single!;
                        single = null;
                    }
                    else if (count == scratch.Length)
                    {
                        Array.Resize(ref scratch, scratch.Length * 2);
                    }

                    scratch[count++] = tween;
                }
            }

            if (activeSlotCount > 0 && slots is { } arr)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    ref TweenSlot s = ref arr[i];
                    if (s.Kind == SlotKind.Empty || s.IsFinished || s.Channel != channel)
                        continue;

                    s.IsPlaying = false;
                    s.IsKilled = true;
                    activeSlotCount--;

                    Action? onKill = s.OnKill;
                    TaskCompletionSource? tcs = s.Finished;
                    s.Target = null;
                    s.OnStart = null;
                    s.OnComplete = null;
                    s.OnKill = null;
                    s.OnLoop = null;
                    s.Finished = null;

                    if (onKill is null && tcs is null)
                        continue;

                    if (slotKillCount == 0)
                    {
                        singleSlotKill = onKill;
                        singleSlotTcs = tcs;
                        slotKillCount = 1;
                    }
                    else
                    {
                        if (slotKillScratch is null)
                        {
                            slotKillScratch = killCallbackScratch;
                            slotTcsScratch = killTcsScratch;
                            killCallbackScratch = null;
                            killTcsScratch = null;
                            if (slotKillScratch is null || slotKillScratch.Length < 4)
                            {
                                slotKillScratch = new Action?[4];
                                slotTcsScratch = new TaskCompletionSource?[4];
                            }
                            slotKillScratch[0] = singleSlotKill;
                            slotTcsScratch![0] = singleSlotTcs;
                            singleSlotKill = null;
                            singleSlotTcs = null;
                        }
                        else if (slotKillCount == slotKillScratch.Length)
                        {
                            int next = slotKillScratch.Length * 2;
                            Array.Resize(ref slotKillScratch, next);
                            Array.Resize(ref slotTcsScratch, next);
                        }

                        slotKillScratch[slotKillCount] = onKill;
                        slotTcsScratch![slotKillCount] = tcs;
                        slotKillCount++;
                    }
                }
            }
        }

        if (count == 1)
        {
            single!.Kill();
        }
        else if (count > 1)
        {
            try
            {
                for (int i = 0; i < count; i++)
                    scratch![i].Kill();
            }
            finally
            {
                Array.Clear(scratch!, 0, count);
                lock (tweensLock)
                {
                    if (killScratch is null || scratch!.Length > killScratch.Length)
                        killScratch = scratch;
                }
            }
        }

        if (slotKillCount == 1)
        {
            singleSlotKill?.Invoke();
            singleSlotTcs?.TrySetResult();
        }
        else if (slotKillCount > 1)
        {
            try
            {
                for (int i = 0; i < slotKillCount; i++)
                {
                    slotKillScratch![i]?.Invoke();
                    slotTcsScratch![i]?.TrySetResult();
                }
            }
            finally
            {
                Array.Clear(slotKillScratch!, 0, slotKillCount);
                Array.Clear(slotTcsScratch!, 0, slotKillCount);
                lock (tweensLock)
                {
                    if (killCallbackScratch is null || slotKillScratch!.Length > killCallbackScratch.Length)
                    {
                        killCallbackScratch = slotKillScratch;
                        killTcsScratch = slotTcsScratch;
                    }
                }
            }
        }
    }

    /// <inheritdoc cref="Kill(TweenChannel)"/>
    public void Kill(object channel) => Kill(new TweenChannel(0, channel));

    /// <summary>Kills every tween, leaving everything where it is right now.</summary>
    public void KillAll()
    {
        Tween[] snapshot;
        List<(Action? OnKill, TaskCompletionSource? Tcs)>? slotCallbacks = null;

        lock (tweensLock)
        {
            snapshot = [.. tweens];
            if (activeSlotCount > 0 && slots is { } arr)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    ref TweenSlot s = ref arr[i];
                    if (s.Kind == SlotKind.Empty || s.IsFinished)
                        continue;

                    s.IsPlaying = false;
                    s.IsKilled = true;
                    activeSlotCount--;

                    if (s.OnKill is not null || s.Finished is not null)
                        (slotCallbacks ??= []).Add((s.OnKill, s.Finished));

                    s.Target = null;
                    s.OnStart = null;
                    s.OnComplete = null;
                    s.OnKill = null;
                    s.OnLoop = null;
                    s.Finished = null;
                }
            }
        }

        foreach (var tween in snapshot)
            tween.Kill();

        if (slotCallbacks is not null)
        {
            foreach (var (onKill, tcs) in slotCallbacks)
            {
                onKill?.Invoke();
                tcs?.TrySetResult();
            }
        }
    }

    /// <summary>Jumps every tween to its end.</summary>
    public void CompleteAll()
    {
        Tween[] snapshot;
        List<(int Index, int Generation)>? activeSlots = null;

        lock (tweensLock)
        {
            snapshot = [.. tweens];
            if (activeSlotCount > 0 && slots is { } arr)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    ref readonly TweenSlot s = ref arr[i];
                    if (s.Kind == SlotKind.Empty || s.IsFinished)
                        continue;
                    (activeSlots ??= []).Add((i, s.Generation));
                }
            }
        }

        foreach (var tween in snapshot)
            tween.Complete();

        if (activeSlots is not null)
        {
            foreach (var (idx, gen) in activeSlots)
                CompleteSlot(idx, gen);
        }
    }

    internal void Adopt(Tween tween)
    {
        lock (tweensLock)
        {
            if (!tweens.Contains(tween))
                tweens.Add(tween);
        }
    }

    private int AllocateSlotLocked(out int generation)
    {
        if (slots is null)
        {
            slots = new TweenSlot[4];
        }

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].Kind == SlotKind.Empty || slots[i].IsFinished)
            {
                generation = nextGeneration++;
                if (generation == 0) generation = nextGeneration++;
                slots[i] = new TweenSlot { Generation = generation };
                return i;
            }
        }

        int index = slots.Length;
        Array.Resize(ref slots, slots.Length * 2);
        generation = nextGeneration++;
        if (generation == 0) generation = nextGeneration++;
        slots[index] = new TweenSlot { Generation = generation };
        return index;
    }

    private void TickSlot(int idx, int gen, float dt)
    {
        bool needStart = false;
        SlotKind kind;
        object? target;
        Delegate? getter;
        Action? onStart = null;

        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length)
                return;
            ref TweenSlot s = ref slots[idx];
            if (s.Generation != gen || !s.IsPlaying || s.IsFinished)
                return;

            if (s.Loops > 0)
                MovedFinite = true;

            dt *= s.TimeScale;

            if (s.DelayLeft > 0.0f)
            {
                s.DelayLeft -= dt;
                if (s.DelayLeft > 0.0f)
                    return;

                dt = -s.DelayLeft;
            }

            if (!s.Started)
            {
                s.Started = true;
                needStart = true;
            }

            kind = s.Kind;
            target = s.Target;
            getter = s.Getter;
            onStart = needStart ? s.OnStart : null;
        }

        if (needStart)
        {
            if (kind != SlotKind.ShakeVector2 && target is not null && getter is not null)
            {
                Vector4 from = ReadSlotValue(kind, getter, target);
                lock (tweensLock)
                {
                    if (slots is null || slots[idx].Generation != gen || slots[idx].IsKilled)
                        return;
                    slots[idx].From = from;
                }
            }

            onStart?.Invoke();
        }

        while (true)
        {
            float seekTime;
            bool hitEnd;
            bool finishedNow = false;
            int loopArg = 0;
            Action<int>? onLoop = null;
            Action? onComplete = null;
            TaskCompletionSource? tcs = null;

            lock (tweensLock)
            {
                if (slots is null || slots[idx].Generation != gen || !slots[idx].IsPlaying || slots[idx].IsFinished)
                    return;

                ref TweenSlot s = ref slots[idx];
                s.Elapsed += dt;
                dt = 0.0f;

                float total = s.TotalDuration;
                if (s.Elapsed >= total)
                {
                    hitEnd = true;
                    seekTime = s.Backwards ? 0.0f : total;
                }
                else
                {
                    hitEnd = false;
                    seekTime = s.Backwards ? total - s.Elapsed : s.Elapsed;
                }
            }

            SeekSlot(idx, gen, seekTime);

            if (!hitEnd)
                return;

            lock (tweensLock)
            {
                if (slots is null || slots[idx].Generation != gen || slots[idx].IsFinished)
                    return;

                ref TweenSlot s = ref slots[idx];
                s.LoopsDone++;
                if (s.Loops > 0 && s.LoopsDone >= s.Loops)
                {
                    s.IsPlaying = false;
                    s.IsCompleted = true;
                    activeSlotCount--;
                    onComplete = s.OnComplete;
                    tcs = s.Finished;
                    s.Target = null;
                    s.OnStart = null;
                    s.OnComplete = null;
                    s.OnKill = null;
                    s.OnLoop = null;
                    s.Finished = null;
                    finishedNow = true;
                }
                else
                {
                    loopArg = s.LoopsDone;
                    onLoop = s.OnLoop;

                    if (s.LoopMode == LoopMode.PingPong)
                        s.Backwards = !s.Backwards;
                    else
                        s.Step2Begun = false;

                    float total = s.TotalDuration;
                    if (total <= 0.0f)
                    {
                        s.Elapsed = 0.0f;
                        onLoop?.Invoke(loopArg);
                        return;
                    }

                    s.Elapsed -= total;
                }
            }

            if (finishedNow)
            {
                onComplete?.Invoke();
                tcs?.TrySetResult();
                return;
            }

            onLoop?.Invoke(loopArg);
        }
    }

    private void SeekSlot(int idx, int gen, float time)
    {
        SlotKind kind;
        object? target;
        Delegate? getter;
        Delegate? setter;
        Vector4 from, to, to2;
        float duration, duration2;
        Easing easing, easing2;
        bool step2Begun;

        lock (tweensLock)
        {
            if (slots is null || slots[idx].Generation != gen || slots[idx].IsFinished)
                return;

            ref readonly TweenSlot s = ref slots[idx];
            kind = s.Kind;
            target = s.Target;
            getter = s.Getter;
            setter = s.Setter;
            from = s.From;
            to = s.To;
            to2 = s.To2;
            duration = s.Duration;
            duration2 = s.Duration2;
            easing = s.Easing;
            easing2 = s.Easing2;
            step2Begun = s.Step2Begun;
        }

        if (target is null || setter is null)
            return;

        switch (kind)
        {
            case SlotKind.Float:
            {
                float t = duration <= 0.0f ? 1.0f : time / duration;
                float val = from.X + (to.X - from.X) * Ease.Apply(easing, t);
                ((Action<object, float>)setter)(target, val);
                break;
            }
            case SlotKind.Vector2:
            {
                float t = duration <= 0.0f ? 1.0f : time / duration;
                float e = Ease.Apply(easing, t);
                var val = new Vector2(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e);
                ((Action<object, Vector2>)setter)(target, val);
                break;
            }
            case SlotKind.Vector3:
            {
                float t = duration <= 0.0f ? 1.0f : time / duration;
                float e = Ease.Apply(easing, t);
                var val = new Vector3(
                    from.X + (to.X - from.X) * e,
                    from.Y + (to.Y - from.Y) * e,
                    from.Z + (to.Z - from.Z) * e);
                ((Action<object, Vector3>)setter)(target, val);
                break;
            }
            case SlotKind.Vector4:
            {
                float t = duration <= 0.0f ? 1.0f : time / duration;
                float e = Ease.Apply(easing, t);
                ((Action<object, Vector4>)setter)(target, from + (to - from) * e);
                break;
            }
            case SlotKind.TwoStepVector2:
            {
                var setV2 = (Action<object, Vector2>)setter;
                if (time < duration)
                {
                    float t = duration <= 0.0f ? 1.0f : time / duration;
                    float e = Ease.Apply(easing, t);
                    setV2(target, new Vector2(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e));
                }
                else
                {
                    Vector2 step2From;
                    if (!step2Begun)
                    {
                        setV2(target, new Vector2(to.X, to.Y));
                        step2From = getter is not null ? ((Func<object, Vector2>)getter)(target) : new Vector2(to.X, to.Y);
                        lock (tweensLock)
                        {
                            if (slots is not null && slots[idx].Generation == gen && !slots[idx].IsFinished)
                            {
                                slots[idx].Step2Begun = true;
                                slots[idx].From = new Vector4(step2From.X, step2From.Y, 0.0f, 0.0f);
                            }
                        }
                    }
                    else
                    {
                        step2From = new Vector2(from.X, from.Y);
                    }

                    float local = MathF.Max(0.0f, time - duration);
                    float t = duration2 <= 0.0f ? 1.0f : MathF.Min(local / duration2, 1.0f);
                    float e = Ease.Apply(easing2, t);
                    setV2(target, new Vector2(
                        step2From.X + (to2.X - step2From.X) * e,
                        step2From.Y + (to2.Y - step2From.Y) * e));
                }
                break;
            }
            case SlotKind.ShakeVector2:
            {
                float t = duration <= 0.0f ? 1.0f : time / duration;
                float left = 1.0f - Ease.Apply(easing, t);
                Vector2 home = new(from.X, from.Y);
                float strength = from.Z;
                Vector2 offset = new Vector2(
                    Random.Shared.NextSingle() * 2.0f - 1.0f,
                    Random.Shared.NextSingle() * 2.0f - 1.0f) * (strength * left);
                ((Action<object, Vector2>)setter)(target, home + offset);
                break;
            }
        }
    }

    private static Vector4 ReadSlotValue(SlotKind kind, Delegate getter, object target) => kind switch
    {
        SlotKind.Float => new Vector4(((Func<object, float>)getter)(target), 0.0f, 0.0f, 0.0f),
        SlotKind.Vector2 or SlotKind.TwoStepVector2 => ToVector4(((Func<object, Vector2>)getter)(target)),
        SlotKind.Vector3 => ToVector4(((Func<object, Vector3>)getter)(target)),
        SlotKind.Vector4 => ((Func<object, Vector4>)getter)(target),
        _ => Vector4.Zero
    };

    private static Vector4 ToVector4(Vector2 v) => new(v.X, v.Y, 0.0f, 0.0f);
    private static Vector4 ToVector4(Vector3 v) => new(v.X, v.Y, v.Z, 0.0f);

    /* Slot accessors for TweenHandle */

    internal float GetSlotDuration(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen ? slots[idx].TotalDuration : 0.0f;
    }

    internal Easing GetSlotEasing(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen ? slots[idx].Easing : Easing.Linear;
    }

    internal int GetSlotLoops(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen ? slots[idx].Loops : 1;
    }

    internal LoopMode GetSlotLoopMode(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen ? slots[idx].LoopMode : LoopMode.Restart;
    }

    internal float GetSlotTimeScale(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen ? slots[idx].TimeScale : 1.0f;
    }

    internal float GetSlotDelay(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen ? slots[idx].Delay : 0.0f;
    }

    internal bool GetSlotIsPlaying(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen && slots[idx].IsPlaying;
    }

    internal bool GetSlotIsCompleted(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen && slots[idx].IsCompleted;
    }

    internal bool GetSlotIsKilled(int idx, int gen)
    {
        lock (tweensLock)
            return slots is not null && (uint)idx < (uint)slots.Length && slots[idx].Generation == gen && slots[idx].IsKilled;
    }

    internal bool GetSlotIsFinished(int idx, int gen)
    {
        lock (tweensLock)
            return slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen || slots[idx].IsFinished;
    }

    internal void SetSlotEasing(int idx, int gen, Easing easing)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].Easing = easing;
            if (slots[idx].Kind == SlotKind.TwoStepVector2)
                slots[idx].Easing2 = easing;
        }
    }

    internal void SetSlotLoops(int idx, int gen, int loops, LoopMode mode)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].Loops = loops == 0 ? 1 : loops;
            slots[idx].LoopMode = mode;
        }
    }

    internal void SetSlotTimeScale(int idx, int gen, float timeScale)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].TimeScale = MathF.Max(0.0f, timeScale);
        }
    }

    internal void SetSlotDelay(int idx, int gen, float seconds)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            float delay = MathF.Max(0.0f, seconds);
            slots[idx].Delay = delay;
            if (!slots[idx].Started)
                slots[idx].DelayLeft = delay;
        }
    }

    internal void AddSlotOnStart(int idx, int gen, Action callback)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].OnStart += callback;
        }
    }

    internal void AddSlotOnComplete(int idx, int gen, Action callback)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].OnComplete += callback;
        }
    }

    internal void AddSlotOnKill(int idx, int gen, Action callback)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].OnKill += callback;
        }
    }

    internal void AddSlotOnLoop(int idx, int gen, Action<int> callback)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].OnLoop += callback;
        }
    }

    internal void PlaySlot(int idx, int gen)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            ref TweenSlot s = ref slots[idx];
            if (s.IsFinished)
            {
                if (s.Target is null) return;
                RewindSlot(ref s);
                activeSlotCount++;
            }
            else if (!s.Started)
            {
                RewindSlot(ref s);
            }

            s.IsPlaying = true;
        }
    }

    internal void PauseSlot(int idx, int gen)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            slots[idx].IsPlaying = false;
        }
    }

    internal void KillSlot(int idx, int gen)
    {
        Action? onKill;
        TaskCompletionSource? tcs;

        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen || slots[idx].IsFinished)
                return;

            ref TweenSlot s = ref slots[idx];
            s.IsPlaying = false;
            s.IsKilled = true;
            activeSlotCount--;
            onKill = s.OnKill;
            tcs = s.Finished;
            s.Target = null;
            s.OnStart = null;
            s.OnComplete = null;
            s.OnKill = null;
            s.OnLoop = null;
            s.Finished = null;
        }

        onKill?.Invoke();
        tcs?.TrySetResult();
    }

    internal void CompleteSlot(int idx, int gen)
    {
        bool needStart = false;
        SlotKind kind;
        object? target;
        Delegate? getter;
        Action? onStart = null;
        float seekTime;

        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen || slots[idx].IsFinished)
                return;

            ref TweenSlot s = ref slots[idx];
            if (!s.Started)
            {
                s.Started = true;
                needStart = true;
                onStart = s.OnStart;
            }

            kind = s.Kind;
            target = s.Target;
            getter = s.Getter;
            bool endsAtStart = s.LoopMode == LoopMode.PingPong && s.Loops > 0 && s.Loops % 2 == 0;
            seekTime = endsAtStart ? 0.0f : s.TotalDuration;
        }

        if (needStart)
        {
            if (kind != SlotKind.ShakeVector2 && target is not null && getter is not null)
            {
                Vector4 from = ReadSlotValue(kind, getter, target);
                lock (tweensLock)
                {
                    if (slots is null || slots[idx].Generation != gen || slots[idx].IsKilled)
                        return;
                    slots[idx].From = from;
                }
            }

            onStart?.Invoke();
        }

        SeekSlot(idx, gen, seekTime);

        Action? onComplete;
        TaskCompletionSource? tcs;
        lock (tweensLock)
        {
            if (slots is null || slots[idx].Generation != gen || slots[idx].IsFinished)
                return;

            ref TweenSlot s = ref slots[idx];
            s.IsPlaying = false;
            s.IsCompleted = true;
            activeSlotCount--;
            onComplete = s.OnComplete;
            tcs = s.Finished;
            s.Target = null;
            s.OnStart = null;
            s.OnComplete = null;
            s.OnKill = null;
            s.OnLoop = null;
            s.Finished = null;
        }

        onComplete?.Invoke();
        tcs?.TrySetResult();
    }

    internal void ResetSlot(int idx, int gen)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen) return;
            ref TweenSlot s = ref slots[idx];
            bool wasFinished = s.IsFinished;
            s.IsPlaying = false;
            RewindSlot(ref s);
            if (wasFinished && s.Target is not null)
                activeSlotCount++;
        }
    }

    internal Task AwaitSlot(int idx, int gen)
    {
        lock (tweensLock)
        {
            if (slots is null || (uint)idx >= (uint)slots.Length || slots[idx].Generation != gen || slots[idx].IsFinished)
                return Task.CompletedTask;

            ref TweenSlot s = ref slots[idx];
            return (s.Finished ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private static void RewindSlot(ref TweenSlot s)
    {
        s.Elapsed = 0.0f;
        s.DelayLeft = s.Delay;
        s.LoopsDone = 0;
        s.Started = false;
        s.Backwards = false;
        s.Step2Begun = false;
        s.IsCompleted = false;
        s.IsKilled = false;
    }
}
