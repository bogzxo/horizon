using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Horizon.Core.Components;
using Horizon.Core.Primitives;

namespace Horizon.Core;

/// <summary>
/// The thing a game is made of: it has components that do its work and children that are entities themselves,
/// and passes drawing and updating on to both.
/// <para>
/// An entity is made (its constructor, on any thread, with nothing of the GPU to be had), then set up (its
/// <see cref="Initialize"/>, on the render thread, where it makes what it needs on the GPU and adds what it is
/// made of), then drawn and updated, and in the end disposed of. Whatever is added to it is set up after it,
/// in the order it was added, and before it is first updated or drawn: see <see cref="InitializeAll"/> for what
/// is added while it is being set up, and <see cref="EntityLifecycle"/> for what is added afterwards.
/// </para>
/// </summary>
public abstract class Entity : IRenderable, IUpdateable, IDisposable, IInstantiable
{
    // Backing store for Thread-Safe Enable/Disable
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public virtual string Name { get; protected set; } = string.Empty;

    public Entity? Parent { get; set; }

    // Use IReadOnlyList so external classes can't bypass our thread-safe Add/Remove methods.
    public IReadOnlyList<IGameComponent> Components => _componentsCache;

    public IReadOnlyList<Entity> Children => _childrenCache;

    // Mutex lock for structural modifications (Add/Remove)
    private readonly Lock _structuralLock = new();

    private readonly List<IGameComponent> _components = [];
    private IGameComponent[] _componentsCache = []; // Fast lock-free iteration cache

    private readonly List<Entity> _children = [];
    private Entity[] _childrenCache = []; // Fast lock-free iteration cache

    // Thread-safe queues and O(1) lookups for initialization
    private readonly ConcurrentQueue<IInstantiable> _uninitializedQueue = new();

    private readonly ConcurrentDictionary<IInstantiable, byte> _uninitializedSet = new();

    private int _initialized = 0; // Thread-safe boolean (0 = false, 1 = true)

    // Whether the entity has been set up. What is added to it from then on is set up at the next frame
    // rather than along with it
    private volatile bool _live;
    private volatile bool _disposed;

    /// <summary>Whether the entity has been set up, and everything that was added to it before that with it.</summary>
    public bool IsInitialized => _live;

    /// <summary>Whether the entity has been disposed of. It is not drawn, updated or added to any more.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Called after the constructor, guaranteeing that there will be a valid GL context.
    /// Calls PostInit after it is complete, do NOT forget base.Initialize()!!!
    /// </summary>
    public virtual void Initialize()
    {
        // Thread-safe initialization guard
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        PostInit();
    }

    /// <summary>
    /// A method that executes after all initialisation is complete.
    /// </summary>
    public virtual void PostInit()
    { }

    public virtual void Render(float dt, object? obj = null)
    {
        // Set up by now, all of it, unless whoever draws this made it and never said so
        InitializeAll();

        // Lock-free span iteration using the cache array
        var entSpan = _childrenCache.AsSpan();
        foreach (var ent in entSpan)
        {
            if (_uninitializedSet.ContainsKey(ent)) continue;

            ent.Render(dt, obj);
        }

        var compSpan = _componentsCache.AsSpan();
        foreach (var comp in compSpan)
        {
            if (_uninitializedSet.ContainsKey(comp)) continue;
            comp.Render(dt, obj);
        }
    }

    /// <summary>
    /// Sets up everything that was added to the entity and is waiting for it, in the order it was added, and then
    /// everything those added in turn, all the way down: when this returns there is nothing left in the entity
    /// that isn't set up. Render thread. Costs nothing when nothing is waiting.
    /// </summary>
    public void InitializeAll()
    {
        if (_live && _uninitializedQueue.IsEmpty) return;
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
                    case IGameComponent comp:
                        comp.Enabled = true;
                        break;

                    case Entity ent:
                        ent.Enabled = true;
                        (entities ??= []).Add(ent);
                        break;
                }

                // Remove from the set only AFTER it is fully initialized
                _uninitializedSet.TryRemove(result, out _);
            }

            // What those added while they were set up comes after all of them, one entity at a time
            if (entities is not null)
            {
                foreach (Entity ent in entities)
                    ent.InitializeAll();
            }
        }
    }

    public virtual void UpdatePhysics(float dt)
    {
        var compSpan = _componentsCache.AsSpan();
        foreach (var comp in compSpan)
        {
            if (_uninitializedSet.ContainsKey(comp)) continue;
            comp.UpdatePhysics(dt);
        }

        var entSpan = _childrenCache.AsSpan();
        foreach (var t in entSpan)
        {
            if (_uninitializedSet.ContainsKey(t)) continue;
            t.UpdatePhysics(dt);
        }
    }

    public virtual void UpdateState(float dt)
    {
        var compSpan = _componentsCache.AsSpan();
        foreach (var comp in compSpan)
        {
            if (_uninitializedSet.ContainsKey(comp)) continue;
            comp.UpdateState(dt);
        }

        var entSpan = _childrenCache.AsSpan();
        foreach (var ent in entSpan)
        {
            if (_uninitializedSet.ContainsKey(ent)) continue;
            ent.UpdateState(dt);
        }
    }

    public void RemoveEntity(in Entity ent)
    {
        lock (_structuralLock)
        {
            if (_children.Remove(ent))
            {
                _childrenCache = [.. _children];
            }
        }
    }

    public void RemoveComponent(IGameComponent comp)
    {
        lock (_structuralLock)
        {
            if (_components.Remove(comp)) // We remove from the PRIVATE list
            {
                // Then we update the cache for the render thread
                _componentsCache = [.. _components];
            }
        }
    }

    /// <summary>
    /// Attempts to return a reference to a specified type of Component lock-free.
    /// </summary>
    public T? GetComponent<T>() where T : IGameComponent
    {
        var span = _componentsCache.AsSpan();
        foreach (var comp in span)
        {
            if (comp is T typedComp) return typedComp;
        }
        return default;
    }

    /// <summary>
    /// Attempts to find all references to a specified type of Entity lock-free.
    /// </summary>
    public List<Entity> GetEntities<T>() where T : Entity
    {
        var result = new List<Entity>();
        var span = _childrenCache.AsSpan();
        foreach (var ent in span)
        {
            if (ent is T typedEnt) result.Add(typedEnt);
        }
        return result;
    }

    /// <summary>
    /// Attempts to return a reference to a specified type of Entity lock-free.
    /// </summary>
    public T? GetEntity<T>() where T : Entity
    {
        var span = _childrenCache.AsSpan();
        foreach (var ent in span)
        {
            if (ent is T typedEnt) return typedEnt;
        }
        return null;
    }

    /// <summary>
    /// Attempts to attach a component to this Entity.
    /// </summary>
    public T AddComponent<T>(T component) where T : IGameComponent
    {
        PushToInitializationQueue(component);

        component.Parent = this;
        component.Enabled = false;
        if (component.Name.Length == 0) component.Name = component.GetType().Name;

        lock (_structuralLock)
        {
            if (_components.Contains(component)) return component;
            _components.Add(component);
            _componentsCache = [.. _components]; // Update the thread-safe read cache
        }
        return component;
    }

    public T AddComponent<T>() where T : IGameComponent, new() =>
        AddComponent(new T());

    public void PushToInitializationQueue(in IInstantiable entity)
    {
        // Set up already (it was somewhere else before, or is added a second time): once is enough
        if (entity is Entity { IsInitialized: true }) return;

        // TryAdd prevents double-queuing efficiently
        if (_uninitializedSet.TryAdd(entity, 1))
        {
            _uninitializedQueue.Enqueue(entity);

            // We are set up ourselves, so nobody is going to come by for this on their own
            if (_live) EntityLifecycle.NoteDirty(this);
        }
    }

    /// <summary>
    /// Attempts to attach a child entity to this Entity.
    /// </summary>
    public T AddEntity<T>(in T entity) where T : Entity
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
            _childrenCache = [.. _children]; // Update the thread-safe read cache
        }

        return entity;
    }

    public T AddEntity<T>() where T : Entity, new() =>
        AddEntity(new T());

    protected virtual void DisposeOther()
    { }

    /// <summary>
    /// Disposes of the entity and everything in it: its components (the ones that can be disposed of), its
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

        IGameComponent[] compsToDispose;
        Entity[] childrenToDispose;

        // Safely extract all items and clear the collections
        lock (_structuralLock)
        {
            compsToDispose = _componentsCache;
            _components.Clear();
            _componentsCache = [];

            childrenToDispose = _childrenCache;
            _children.Clear();
            _childrenCache = [];
        }

        foreach (var item in compsToDispose)
        {
            if (item is IDisposable managedItem)
                managedItem.Dispose();
        }

        foreach (var item in childrenToDispose)
        {
            if (item is IDisposable managedItem)
                managedItem.Dispose();
        }

        DisposeOther();
        GC.SuppressFinalize(this);
    }
}