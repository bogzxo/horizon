using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

using Horizon.Core.Components;
using Horizon.Core.Diagnostics;
using Horizon.Core.Primitives;
using Horizon.Core.Threading;
using Horizon.Core.Tweening;

namespace Horizon.Core;

/// <summary>
/// The thing a game is made of. It has components that do its work and children that are entities themselves,
/// and passes drawing and updating on to both.
/// <para>
/// An entity is made (its constructor, on any thread, with nothing of the GPU to be had), then set up (its
/// <see cref="Initialize"/>, on the render thread, where it makes what it needs on the GPU and adds what it is
/// made of), then drawn and updated, and in the end disposed of. Whatever is added to it is set up after it,
/// in the order it was added, and before it is first updated or drawn. See <see cref="InitializeAll"/> for what
/// is added while it is being set up, and <see cref="EntityLifecycle"/> for what is added afterwards.
/// </para>
/// <para>
/// Children and components that are switched off (<see cref="Enabled"/>) are skipped, with everything in them.
/// </para>
/// </summary>
public abstract class Entity : IRenderable, IUpdateable, IDisposable, IInstantiable
{
    private volatile bool _enabled = true;

    /// <summary>
    /// Whether the entity gets its turns. One that is switched off is not updated and not drawn, and neither is anything in it.
    /// It is off until it has been set up.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public virtual string Name { get; protected set; } = string.Empty;

    public Entity? Parent { get; set; }

    // Read only, so nobody adds or removes behind the back of the lock
    public IReadOnlyList<IGameComponent> Components => _componentsCache;

    public IReadOnlyList<Entity> Children => _childrenCache;

    // Held while something is added or removed
    private readonly Lock _structuralLock = new();

    // The lists are only touched under the lock. The arrays are copies that are swapped whole, so a turn can walk them without one
    private readonly List<IGameComponent> _components = [];
    private IGameComponent[] _componentsCache = [];

    private readonly List<Entity> _children = [];
    private Entity[] _childrenCache = [];

    // What was added and is still waiting to be set up, in the order it came. And how many of those there are,
    // which is nearly always none, so a turn only has to look anything up while something is waiting
    private readonly ConcurrentQueue<IInstantiable> _uninitializedQueue = new();
    private readonly ConcurrentDictionary<IInstantiable, byte> _uninitializedSet = new();
    private int _waiting;

    private int _initialized;

    // Whether the entity has been set up. What is added to it from then on is set up at the next frame
    // rather than along with it
    private volatile bool _live;
    private volatile bool _disposed;

    private TweenContext? _tweens;

    // What the entity is made of as of every snapshot, which is what a frame that is drawn from snapshots walks. The
    // lists are the copies that are swapped whole, so holding on to them costs nothing and they never change underneath
    private readonly Snapshot<Node> _node = new();

    /// <summary>
    /// The children and components of an entity as they were captured, and which of the components were switched on
    /// (one bit each for the first 64, the ones after that go by whether they are switched on when they are drawn).
    /// </summary>
    private readonly record struct Node(Entity[] Children, IGameComponent[] Components, ulong ComponentsOn);

    /// <summary>Whether the entity has been set up, and everything that was added to it before that with it.</summary>
    public bool IsInitialized => _live;

    /// <summary>Whether the entity has been disposed of. It is not drawn, updated or added to any more.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// The tweens of the entity, for animating whatever it has. They move along with its updates, and stop when it is switched off.
    /// Made the first time somebody asks.
    /// </summary>
    [HideInInspector]
    public TweenContext Tweens => _tweens ?? Interlocked.CompareExchange(ref _tweens, new TweenContext(), null) ?? _tweens;

    /// <summary>
    /// Called after the constructor, on the render thread, with the GPU there to be talked to.
    /// Calls PostInit after it is complete, do NOT forget base.Initialize()!!!
    /// </summary>
    public virtual void Initialize()
    {
        // Only the first time counts
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        PostInit();
    }

    /// <summary>
    /// A method that executes after all initialisation is complete.
    /// </summary>
    public virtual void PostInit()
    { }

    public virtual void Render(float dt)
    {
        // Drawn alongside the simulation, from what it published, so nothing here may be set up or looked at as it is now
        RenderFrame frame = RenderFrame.Active;
        if (frame.IsDecoupled)
        {
            RenderCaptured(frame, dt);
            return;
        }

        // Set up by now, all of it, unless whoever draws this made it and never said so
        InitializeAll();

        bool waiting = Volatile.Read(ref _waiting) > 0;

        foreach (Entity child in _childrenCache)
        {
            if (!child.Enabled || (waiting && _uninitializedSet.ContainsKey(child))) continue;
            child.Render(dt);
        }

        foreach (IGameComponent component in _componentsCache)
        {
            if (!component.Enabled || (waiting && _uninitializedSet.ContainsKey(component))) continue;
            component.Render(dt);
        }
    }

    /// <summary>
    /// Helper method to draw the children and components the entity had in the newer snapshot of a frame, the ones of
    /// them that were captured.
    /// </summary>
    private void RenderCaptured(in RenderFrame frame, float dt)
    {
        if (!_node.TryGet(frame, out Node node)) return;

        foreach (Entity child in node.Children)
        {
            // Gone since (it is freed on this thread, before a frame is drawn), or not there to be drawn when it was captured
            if (child._disposed || !child.WasCaptured(frame)) continue;
            child.Render(dt);
        }

        IGameComponent[] components = node.Components;
        for (int i = 0; i < components.Length; i++)
        {
            bool on = i < 64 ? (node.ComponentsOn & (1UL << i)) != 0 : components[i].Enabled;
            if (on) components[i].Render(dt);
        }
    }

    /// <summary>
    /// Whether the entity was there to be drawn (set up and switched on) when the newer snapshot of a frame was captured.
    /// Render thread.
    /// </summary>
    public bool WasCaptured(in RenderFrame frame) => _node.TryGet(frame, out _);

    /// <summary>
    /// Publishes what the entity is made of, and has its children and components publish whatever they draw. Called on
    /// the simulation thread at the end of every tick, for everything that is set up and switched on, the way
    /// <see cref="UpdateState"/> is. An entity that draws something of its own publishes it here (and calls this base
    /// method, or nothing in it is drawn), see <see cref="Snapshot{T}"/>.
    /// </summary>
    public virtual void Capture()
    {
        bool waiting = Volatile.Read(ref _waiting) > 0;

        IGameComponent[] components = _componentsCache;
        ulong on = 0;
        for (int i = 0; i < components.Length; i++)
        {
            IGameComponent component = components[i];
            if (!component.Enabled || (waiting && _uninitializedSet.ContainsKey(component))) continue;

            if (i < 64) on |= 1UL << i;
            if (component is not ISnapshotSource source) continue;

            // Measured while anybody is counting what things allocate, see AllocationLog. A UI paints here
            if (AllocationLog.Tracking)
            {
                long before = AllocationLog.Begin(out long nested);
                source.Capture();
                AllocationLog.End(component, before, nested);
            }
            else
            {
                source.Capture();
            }
        }

        Entity[] children = _childrenCache;
        foreach (Entity child in children)
        {
            if (!child.Enabled || (waiting && _uninitializedSet.ContainsKey(child))) continue;

            if (AllocationLog.Tracking)
            {
                long before = AllocationLog.Begin(out long nested);
                child.Capture();
                AllocationLog.End(child, before, nested);
            }
            else
            {
                child.Capture();
            }
        }

        _node.Publish(new Node(children, components, on));
    }

    /// <summary>
    /// Sets up everything that was added to the entity and is waiting for it, in the order it was added, and then
    /// everything those added in turn, all the way down. When this returns there is nothing left in the entity
    /// that isn't set up. Render thread. Costs nothing when nothing is waiting.
    /// </summary>
    public void InitializeAll()
    {
        if (_live && Volatile.Read(ref _waiting) == 0) return;
        if (_disposed) return;

        _live = true;

        List<Entity>? entities = null;
        while (!_uninitializedQueue.IsEmpty)
        {
            entities?.Clear();

            while (_uninitializedQueue.TryDequeue(out IInstantiable? result))
            {
                result.Initialize();

                switch (result)
                {
                    case IGameComponent component:
                        component.Enabled = true;
                        break;

                    case Entity entity:
                        entity.Enabled = true;
                        (entities ??= []).Add(entity);
                        break;
                }

                // Only once it is all there does it get its turns
                if (_uninitializedSet.TryRemove(result, out _))
                    Interlocked.Decrement(ref _waiting);
            }

            // What those added while they were set up comes after all of them, one entity at a time
            if (entities is not null)
            {
                foreach (Entity entity in entities)
                    entity.InitializeAll();
            }
        }
    }

    public virtual void UpdatePhysics(float dt)
    {
        bool waiting = Volatile.Read(ref _waiting) > 0;

        // With somebody counting what things allocate, the same walk with every stop measured
        if (AllocationLog.Tracking)
        {
            UpdateMeasured(dt, physics: true, waiting);
            return;
        }

        foreach (IGameComponent component in _componentsCache)
        {
            if (!component.Enabled || (waiting && _uninitializedSet.ContainsKey(component))) continue;
            component.UpdatePhysics(dt);
        }

        foreach (Entity child in _childrenCache)
        {
            if (!child.Enabled || (waiting && _uninitializedSet.ContainsKey(child))) continue;
            child.UpdatePhysics(dt);
        }
    }

    /// <summary>
    /// Helper method to update the components and the children the way <see cref="UpdateState"/> and
    /// <see cref="UpdatePhysics"/> do, measuring what each of them allocates on the way, see <see cref="AllocationLog"/>.
    /// Apart from the two so they stay the plain loops they are when nobody is counting, which is always but for
    /// the odd afternoon somebody wants to know where the garbage comes from.
    /// </summary>
    private void UpdateMeasured(float dt, bool physics, bool waiting)
    {
        foreach (IGameComponent component in _componentsCache)
        {
            if (!component.Enabled || (waiting && _uninitializedSet.ContainsKey(component))) continue;

            long before = AllocationLog.Begin(out long nested);
            if (physics) component.UpdatePhysics(dt);
            else component.UpdateState(dt);
            AllocationLog.End(component, before, nested);
        }

        foreach (Entity child in _childrenCache)
        {
            if (!child.Enabled || (waiting && _uninitializedSet.ContainsKey(child))) continue;

            long before = AllocationLog.Begin(out long nested);
            if (physics) child.UpdatePhysics(dt);
            else child.UpdateState(dt);
            AllocationLog.End(child, before, nested);
        }
    }

    public virtual void UpdateState(float dt)
    {
        _tweens?.Tick(dt);

        bool waiting = Volatile.Read(ref _waiting) > 0;

        if (AllocationLog.Tracking)
        {
            UpdateMeasured(dt, physics: false, waiting);
            return;
        }

        foreach (IGameComponent component in _componentsCache)
        {
            if (!component.Enabled || (waiting && _uninitializedSet.ContainsKey(component))) continue;
            component.UpdateState(dt);
        }

        foreach (Entity child in _childrenCache)
        {
            if (!child.Enabled || (waiting && _uninitializedSet.ContainsKey(child))) continue;
            child.UpdateState(dt);
        }
    }

    /// <summary>
    /// Takes a child out of the entity. It is not disposed of, see <see cref="Destroy"/> for that.
    /// </summary>
    /// <returns>Whether it was in there.</returns>
    public bool RemoveEntity(Entity entity)
    {
        lock (_structuralLock)
        {
            if (!_children.Remove(entity)) return false;

            _childrenCache = [.. _children];
        }

        if (ReferenceEquals(entity.Parent, this)) entity.Parent = null;
        return true;
    }

    /// <summary>
    /// Takes a component off the entity. It is not disposed of.
    /// </summary>
    /// <returns>Whether it was on there.</returns>
    public bool RemoveComponent(IGameComponent component)
    {
        lock (_structuralLock)
        {
            if (!_components.Remove(component)) return false;

            _componentsCache = [.. _components];
        }

        return true;
    }

    /// <summary>
    /// Takes the entity out of whatever it is in and disposes of it. From any thread.
    /// It gets no more turns from this moment on. What it holds on the GPU is freed on the render thread at the start of the next frame.
    /// </summary>
    public void Destroy()
    {
        if (_disposed) return;

        _enabled = false;
        Parent?.RemoveEntity(this);

        EntityLifecycle.Retire(this);
    }

    /// <summary>
    /// The first component of a kind, null if the entity has none.
    /// </summary>
    public T? GetComponent<T>() where T : IGameComponent
    {
        foreach (IGameComponent component in _componentsCache)
        {
            if (component is T found) return found;
        }

        return default;
    }

    /// <summary>
    /// The first component of a kind, false if the entity has none.
    /// </summary>
    public bool TryGetComponent<T>(out T component) where T : IGameComponent
    {
        foreach (IGameComponent candidate in _componentsCache)
        {
            if (candidate is not T found) continue;

            component = found;
            return true;
        }

        component = default!;
        return false;
    }

    /// <summary>
    /// Every child of a kind.
    /// </summary>
    public List<T> GetEntities<T>() where T : Entity
    {
        var found = new List<T>();
        foreach (Entity child in _childrenCache)
        {
            if (child is T match) found.Add(match);
        }

        return found;
    }

    /// <summary>
    /// The first child of a kind, null if there is none. Only the children, see <see cref="FindEntity{T}"/> for all the way down.
    /// </summary>
    public T? GetEntity<T>() where T : Entity
    {
        foreach (Entity child in _childrenCache)
        {
            if (child is T match) return match;
        }

        return null;
    }

    /// <summary>
    /// The first entity of a kind anywhere under this one, null if there is none. Children before grandchildren.
    /// </summary>
    public T? FindEntity<T>() where T : Entity
    {
        if (GetEntity<T>() is { } child) return child;

        foreach (Entity entity in _childrenCache)
        {
            if (entity.FindEntity<T>() is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// The nearest entity of a kind above this one, null if there is none. Handy for finding the scene or the renderer something is in.
    /// </summary>
    public T? FindParent<T>() where T : Entity
    {
        for (Entity? at = Parent; at is not null; at = at.Parent)
        {
            if (at is T found) return found;
        }

        return null;
    }

    /// <summary>
    /// Attaches a component to the entity. It is set up before its first turn.
    /// </summary>
    public T AddComponent<T>(T component) where T : IGameComponent
    {
        PushToInitializationQueue(component);

        component.Parent = this;
        component.Enabled = false;
        if (string.IsNullOrEmpty(component.Name)) component.Name = component.GetType().Name;

        lock (_structuralLock)
        {
            if (_components.Contains(component)) return component;

            _components.Add(component);
            _componentsCache = [.. _components];
        }

        return component;
    }

    public T AddComponent<T>() where T : IGameComponent, new() =>
        AddComponent(new T());

    public void PushToInitializationQueue(IInstantiable entity)
    {
        // Set up already (it was somewhere else before, or is added a second time), once is enough
        if (entity is Entity { IsInitialized: true }) return;

        // Not twice in the queue either
        if (!_uninitializedSet.TryAdd(entity, 1)) return;

        Interlocked.Increment(ref _waiting);
        _uninitializedQueue.Enqueue(entity);

        // We are set up ourselves, so nobody is going to come by for this on their own
        if (_live) EntityLifecycle.NoteDirty(this);
    }

    /// <summary>
    /// Adds a child to the entity. It is set up before its first turn.
    /// </summary>
    public T AddEntity<T>(T entity) where T : Entity
    {
        PushToInitializationQueue(entity);

        entity.Parent = this;

        // Off until it has been set up, which one that comes from somewhere else has been already
        if (!entity.IsInitialized) entity.Enabled = false;
        if (entity.Name.Length == 0) entity.Name = entity.GetType().Name;

        lock (_structuralLock)
        {
            if (_children.Contains(entity)) return entity;

            _children.Add(entity);
            _childrenCache = [.. _children];
        }

        return entity;
    }

    public T AddEntity<T>() where T : Entity, new() =>
        AddEntity(new T());

    protected virtual void DisposeOther()
    { }

    /// <summary>
    /// Disposes of the entity and everything in it. That is its components (the ones that can be disposed of), its
    /// children, and then whatever it holds itself (<see cref="DisposeOther"/>). Render thread, as what is freed
    /// is mostly on the GPU. Doing it twice does nothing the second time.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _enabled = false;

        // Whatever was still waiting to be set up is in the lists below as well, and goes with them
        _uninitializedQueue.Clear();
        _uninitializedSet.Clear();
        Volatile.Write(ref _waiting, 0);

        IGameComponent[] components;
        Entity[] children;

        lock (_structuralLock)
        {
            components = _componentsCache;
            _components.Clear();
            _componentsCache = [];

            children = _childrenCache;
            _children.Clear();
            _childrenCache = [];
        }

        foreach (IGameComponent component in components)
        {
            if (component is IDisposable disposable)
                disposable.Dispose();
        }

        foreach (Entity child in children)
            child.Dispose();

        DisposeOther();
        GC.SuppressFinalize(this);
    }
}
