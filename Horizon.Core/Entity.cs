using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Egui;
using Horizon.Core.Components;
using Horizon.Core.Primitives;

namespace Horizon.Core;

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
        InitializeAll();

        // Lock-free span iteration using the cache array
        var entSpan = _childrenCache.AsSpan();
        foreach (var ent in entSpan)
        {
            if (_uninitializedSet.ContainsKey(ent)) continue;

            ent.InitializeAll();

            ent.Render(dt, obj);
        }

        var compSpan = _componentsCache.AsSpan();
        foreach (var comp in compSpan)
        {
            if (_uninitializedSet.ContainsKey(comp)) continue;
            comp.Render(dt, obj);
        }
    }

    public void InitializeAll()
    {
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
                    break;
            }

            // Remove from the set only AFTER it is fully initialized
            _uninitializedSet.TryRemove(result, out _);
        }
    }

    public virtual void RenderUi(Ui root)
    {
        var entSpan = _childrenCache.AsSpan();
        foreach (var entity in entSpan)
        {
            if (_uninitializedSet.ContainsKey(entity)) continue;
            entity.RenderUi(root);
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
        // TryAdd prevents double-queuing efficiently
        if (_uninitializedSet.TryAdd(entity, 1))
        {
            _uninitializedQueue.Enqueue(entity);
        }
    }

    /// <summary>
    /// Attempts to attach a child entity to this Entity.
    /// </summary>
    public T AddEntity<T>(in T entity) where T : Entity
    {
        PushToInitializationQueue(entity);

        entity.Parent = this;
        entity.Enabled = false;
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

    public void Dispose()
    {
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