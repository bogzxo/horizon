using System.Collections.Concurrent;

namespace Horizon.Core;

/// <summary>
/// Keeps track of what has been added to the entities of a running game and is still to be set up.
/// <para>
/// Setting something up (its <c>Initialize</c>) may need the GPU, which only the render thread can talk to, so
/// it can't always happen where the thing is added: a component that is added from an update is set up later.
/// What this guarantees is when. Everything that is waiting is set up in one go at the start of the next frame,
/// before anything is drawn, and neither the logic nor the physics takes another turn until that has happened
/// (see <see cref="WindowManager"/>). So nothing is ever updated or drawn next to something that was added
/// before it and doesn't exist yet: whatever was added during one turn is there, whole, for the next.
/// </para>
/// <para>
/// What is added on the way to being set up itself (the children an entity adds in its own <c>Initialize</c>, or
/// to an entity that hasn't been set up yet) doesn't come through here at all, it is set up along with whatever
/// it was added to: see <see cref="Entity.InitializeAll"/>.
/// </para>
/// </summary>
public static class EntityLifecycle
{
    // The entities that are set up and have had something added to them since
    private static readonly ConcurrentQueue<Entity> dirty = new();
    private static int pending;

    private static int renderThread = -1;

    /// <summary>Whether anything is waiting to be set up. From any thread.</summary>
    public static bool HasPending => Volatile.Read(ref pending) > 0;

    /// <summary>Whether this is the thread that draws, the only one that may set things up.</summary>
    public static bool IsRenderThread => Environment.CurrentManagedThreadId == renderThread;

    /// <summary>
    /// Asked for whatever is to hold while an entity is set up, and disposed of afterwards: for an engine that
    /// keeps track of who makes what, so that what a scene's entities make is noted as that scene's.
    /// </summary>
    public static Func<Entity, IDisposable?>? Scope { get; set; }

    internal static void ClaimRenderThread() => renderThread = Environment.CurrentManagedThreadId;

    internal static void NoteDirty(Entity entity)
    {
        Interlocked.Increment(ref pending);
        dirty.Enqueue(entity);
    }

    /// <summary>
    /// Sets up everything that is waiting, and whatever that adds in turn. Render thread, with nothing else at
    /// work on the game.
    /// </summary>
    public static void Flush()
    {
        while (dirty.TryDequeue(out Entity? entity))
        {
            Interlocked.Decrement(ref pending);

            // Gone in the meantime, and what was added to it with it
            if (entity.IsDisposed)
                continue;

            using IDisposable? scope = Scope?.Invoke(entity);
            entity.InitializeAll();
        }
    }
}
