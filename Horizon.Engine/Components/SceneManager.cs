using System;
using System.Collections.Generic;
using Egui;
using Horizon.Core;

using Horizon.Core.Components;

namespace Horizon.Engine.Components;

// Dropped InstanceManager<Scene> to guarantee no Activator/Reflection warnings during AOT publishing.
public class SceneManager : Entity
{
    // Absorbed from InstanceManager
    public Scene? CurrentInstance { get; private set; }

    private bool _halt = false;

    // AOT-friendly registry for dynamic Type lookups
    private readonly Dictionary<Type, Func<Scene>> _sceneFactories = new();


    /// <summary>
    /// AOT Safe: Registers a factory delegate for a scene type.
    /// Call this during startup if you still need to use ChangeInstance(Type).
    /// </summary>
    public void RegisterScene<TScene>(Func<TScene> factory) where TScene : Scene
    {
        _sceneFactories[typeof(TScene)] = factory;
    }

    /// <summary>
    /// AOT Safe: The modern, preferred way to change scenes without reflection.
    /// </summary>
    public void ChangeInstance<TScene>() where TScene : Scene, new()
    {
        SetScene(new TScene());
    }

    /// <summary>
    /// Drop-in replacement for the old reflection-based method.
    /// Requires you to register the scene via RegisterScene() first.
    /// </summary>
    public void ChangeInstance(Type type)
    {
        if (_sceneFactories.TryGetValue(type, out var factory))
        {
            SetScene(factory());
        }
        else
        {
            throw new InvalidOperationException(
                $"Scene type '{type.Name}' is not registered. For Native AOT, " +
                "use ChangeInstance<T>() or call RegisterScene() during engine startup."
            );
        }
    }

    public void SetScene(in Scene scene)
    {
        CurrentInstance = scene;

        _halt = true;
        if (CurrentInstance is null) return;
        CurrentInstance.Enabled = false;
        CurrentInstance.Parent = Parent; // pass through the engine.
    }

    public override void Render(float dt, object? obj = null)
    {
        CurrentInstance?.Render(dt);
        if (_halt && CurrentInstance is not null)
        {
            CurrentInstance.Initialize();
            CurrentInstance.Enabled = true;
            _halt = false;
        }

        base.Render(dt, obj);
    }

    public override void RenderUi(Ui root)
    {
        if (_halt || !Enabled)
            return;
        CurrentInstance?.RenderUi(root);

        base.RenderUi(root);
    }

    public override void UpdatePhysics(float dt)
    {
        if (_halt || !Enabled)
            return;
        CurrentInstance?.UpdatePhysics(dt);

        base.UpdatePhysics(dt);
    }

    public override void UpdateState(float dt)
    {
        if (_halt || !Enabled)
            return;
        CurrentInstance?.UpdateState(dt);

        base.UpdateState(dt);
    }
}