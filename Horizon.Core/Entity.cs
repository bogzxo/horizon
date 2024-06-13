using System.Runtime.InteropServices;

using Horizon.Core.Components;
using Horizon.Core.Primitives;

namespace Horizon.Core;

public abstract class Entity : IRenderable, IUpdateable, IDisposable, IInstantiable
{
    public bool Enabled { get; set; } = true;
    public virtual string Name { get; protected set; } = string.Empty;

    public Entity Parent { get; set; }

    public List<IGameComponent> Components { get; init; } = [];
    public List<Entity> Children { get; init; } = [];

    private readonly Queue<IInstantiable> _uninitialized = [];

    /// <summary>
    /// Called after the constructor, guaranteeing that there will be a valid GL context. Calls PostInit after it is complete, do NOT forget base.Initialize()!!!
    /// </summary>
    public virtual void Initialize()
    { PostInit(); }

    /// <summary>
    /// A method that executes after all initialisation is complete.
    /// </summary>
    public virtual void PostInit() { }

    public virtual void Render(float dt, object? obj = null)
    {
        InitializeAll();

        if (Children.Count > 0)
        {
            var entSpan = CollectionsMarshal.AsSpan(Children);
            for (int i = 0; i < entSpan.Length; i++)
            {
                if (entSpan[i] is null) continue;

                entSpan[i].InitializeAll();

                entSpan[i].Render(dt);
            }
        }
        if (Components.Count > 0)
        {
            var compSpan = CollectionsMarshal.AsSpan(Components);
            for (int i = 0; i < compSpan.Length; i++)
            {
                if (compSpan[i] is null) continue;
                compSpan[i].Render(dt);
            }
        }
    }

    public void InitializeAll()
    {
        while (_uninitialized.Count > 0)
        {
            if (_uninitialized.TryDequeue(out IInstantiable? result))
            {
                if (result is null)
                    continue;

                result.Initialize();

                if (result is IGameComponent comp)
                    comp.Enabled = true;
                if (result is Entity ent)
                    ent.Enabled = true;
            }
        }
    }

    public virtual void UpdatePhysics(float dt)
    {
        if (Components.Count > 0)
        {
            var compSpan = CollectionsMarshal.AsSpan(Components);
            for (int i = 0; i < compSpan.Length; i++)
            {
                if (compSpan[i] is null) continue;
                compSpan[i].UpdatePhysics(dt);
            }
        }

        if (Children.Count > 0)
        {
            var entSpan = CollectionsMarshal.AsSpan(Children);
            for (int i = 0; i < entSpan.Length; i++)
            {
                if (entSpan[i] is null) continue;
                entSpan[i].UpdatePhysics(dt);
            }
        }
    }

    public virtual void UpdateState(float dt)
    {
        if (Components.Count > 0)
        {
            var compSpan = CollectionsMarshal.AsSpan(Components);
            for (int i = 0; i < compSpan.Length; i++)
            {
                if (compSpan[i] is null) continue;
                compSpan[i].UpdateState(dt);
            }
        }

        if (Children.Count > 0)
        {
            var entSpan = CollectionsMarshal.AsSpan(Children);
            for (int i = 0; i < entSpan.Length; i++)
            {
                if (entSpan[i] is null) continue;
                entSpan[i].UpdateState(dt);
            }
        }
    }

    public void RemoveEntity(in Entity ent)
    {
        Children.Remove(ent);
    }

    /// <summary>
    /// Attempts to return a reference to a specified type of Component.
    /// </summary>
    public T? GetComponent<T>()
        where T : IGameComponent => (T?)Components.Find(comp => comp is T);

    /// <summary>
    /// Attempts to find all reference to a specified type of Entity.
    /// </summary>
    public List<Entity> GetEntities<T>()
        where T : Entity => Children.FindAll(e => e is T);

    /// <summary>
    /// Attempts to return a reference to a specified type of Entity. (if multiple are found, the first one is selected.)
    /// </summary>
    public T? GetEntity<T>()
        where T : Entity => (T?)Children.FindAll(e => e is T).FirstOrDefault();

    /// <summary>
    /// Attempts to attach a component to this Entity.
    /// </summary>
    /// <returns>A reference to the component.</returns>
    public T AddComponent<T>(T component)
        where T : IGameComponent
    {
        if (GetComponent<T>() is null && !_uninitialized.Contains(component))
            _uninitialized.Enqueue(component);

        component.Parent = this;
        component.Enabled = false;
        component.Name ??= component.GetType().Name;

        Components.Add(component);
        return component;
    }

    /// <summary>
    /// Attempts to attach a component to this Entity.
    /// </summary>
    /// <returns>A reference to the component.</returns>
    public T AddComponent<T>()
        where T : IGameComponent, new()
    {
        var component = new T();
        if (component is null)
        {
            // failed to create component.
        }

        return AddComponent((T)component!);
    }

    public void PushToInitializationQueue(in IInstantiable entity) =>
        _uninitialized.Enqueue(entity);

    /// <summary>
    /// Attempts to attach a child entity to this Entity.
    /// </summary>
    /// <returns>A reference to the child entity.</returns>
    public T AddEntity<T>(in T entity)
        where T : Entity
    {
        if (!Children.Contains(entity) && !_uninitialized.Contains(entity))
        {
            _uninitialized.Enqueue(entity);
        }
        else
        {
            // entity already exists, dupe.
        }

        entity.Parent = this;
        entity.Enabled = false;
        entity.Name ??= entity.GetType().Name;

        Children.Add(entity);

        return entity;
    }

    /// <summary>
    /// Attempts to attach a child entity  to this Entity.
    /// </summary>
    /// <returns>A reference to the child entity.</returns>
    public T AddEntity<T>()
        where T : Entity, new()
    {
        var entity = new T();
        if (entity is null)
        {
            // failed to create entity.
        }

        return AddEntity((T)entity!);
    }

    protected virtual void DisposeOther()
    {
    }

    public void Dispose()
    {
        foreach (var item in Components)
        {
            if (item is IDisposable managedItem)
            {
                managedItem.Dispose();
            }
        }

        Components.Clear();

        foreach (var item in Children)
        {
            if (item is IDisposable managedItem)
            {
                managedItem.Dispose();
            }
        }

        Children.Clear();

        DisposeOther();

        GC.SuppressFinalize(this);
    }
}