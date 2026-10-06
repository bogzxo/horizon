using System;
using System.Collections.Generic;
using System.Threading;

using Horizon.Core;

using Horizon.Core.Components;

using Silk.NET.OpenGL;

namespace Horizon.Engine.Components;

// Dropped InstanceManager<Scene> to guarantee no Activator/Reflection warnings during AOT publishing.
/// <summary>
/// Holds the scene that is on screen and swaps it for another one when asked to.
/// <para>
/// A swap happens all at once, on the render thread, at the start of a frame: the new scene is set up, all of it
/// (see <see cref="Entity.InitializeAll"/>), and run until it has everything it shows (see
/// <see cref="ReportUnfinished"/>), and the scene that is left is freed with everything it had on the GPU (see
/// <see cref="Scene.Assets"/>). Only then is a frame drawn. So the frame before a swap is the old scene as it
/// was, the frame after it is the new scene whole, and nothing in between is ever shown or updated: no empty
/// frame, no frame of a scene that is still missing its UI, and no update of a scene that is half set up.
/// </para>
/// </summary>
public class SceneManager : Entity
{
    // The most turns a scene that was just set gets to finish itself before it is shown as it is
    private const int MAX_WARM_UP = 8;

    // Absorbed from InstanceManager
    public Scene? CurrentInstance { get; private set; }

    // Whether a scene is waiting to take over. Nothing is updated in the meantime: the scene that is left has
    // said it is done, and the one that takes over isn't set up yet
    private volatile bool _halt = false;

    // The scene that takes over at the next frame, and what guards it: scenes are set from any thread
    private readonly Lock _changeLock = new();
    private Scene? _incoming;

    // The scenes that have been left and are still to be freed
    private readonly List<Scene> _retired = [];

    // Whether something that was drawn this frame said it isn't all there yet
    private bool _unfinished;

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

    /// <summary>
    /// Has a scene take over from the one that is on screen, at the start of the next frame. From any thread.
    /// Until then <see cref="CurrentInstance"/> is still the scene that is left, which is not updated any more.
    /// </summary>
    public void SetScene(in Scene scene)
    {
        lock (_changeLock)
        {
            // One that was set and never got as far as being shown
            if (_incoming is not null && !ReferenceEquals(_incoming, scene) && !_incoming.Persistent)
            {
                lock (_retired) _retired.Add(_incoming);
            }

            _incoming = scene;
            _halt = true;
        }
    }

    /// <summary>
    /// For whatever is drawn in a scene and finds it can't show all of itself yet: a UI that hasn't been updated
    /// once, art that is still on its way to the GPU. Called while drawing, on the render thread. A scene that
    /// has just been set is not shown while anything in it says this, it is given another turn instead (up to a
    /// point). At any other time it does nothing.
    /// </summary>
    public void ReportUnfinished() => _unfinished = true;

    public override void Render(float dt, object? obj = null)
    {
        if (_halt)
            Change(dt);

        if (CurrentInstance is { } scene)
        {
            // Whatever is made while the scene is drawn (most things are only made when they are first needed)
            // is the scene's
            using (scene.Assets.Enter())
                scene.Render(dt);
        }

        base.Render(dt, obj);
    }

    /// <summary>
    /// Helper method to swap the scene on screen for the one that was set. Render thread, with nothing else at
    /// work on the game: the engine draws and updates in turns.
    /// </summary>
    private void Change(float dt)
    {
        Scene? incoming;
        lock (_changeLock)
        {
            incoming = _incoming;
            _incoming = null;
            _halt = false;
        }

        Scene? left = CurrentInstance;
        if (ReferenceEquals(incoming, left))
        {
            DisposeRetired();
            return;
        }

        CurrentInstance = incoming;

        if (incoming is not null)
        {
            using (incoming.Assets.Enter())
                SetUp(incoming, dt);

            // What the warm up drew is not for showing
            var gl = GameEngine.Instance.GL;
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        // The scene that is left goes last, once the new one has everything it needs: what the two share by name
        // stays where it is that way, rather than being freed and loaded all over again
        if (left is { Persistent: false })
        {
            lock (_retired) _retired.Add(left);
        }

        DisposeRetired();
    }

    /// <summary>
    /// Helper method to set a scene up until it is ready to be shown, with everything it makes noted as its own.
    /// </summary>
    private void SetUp(Scene scene, float dt)
    {
        scene.Enabled = false;
        scene.Parent = Parent; // pass through the engine.

        // The scene, and then everything it added while it was at it, all the way down
        scene.Initialize();
        scene.InitializeAll();
        scene.Enabled = true;

        // Everything in the scene exists now, but a scene isn't whole the moment it does. A lot is only made on
        // the GPU the first time it is drawn, a UI has nothing to draw before it has been updated, and art that
        // is asked for while painting arrives a frame later. Shown right away that is a few frames of a
        // background without its UI. So it is drawn and updated here, unseen, until nothing in it says it is
        // still missing something: all of this ends up in one frame, which is drawn over afterwards
        for (int turn = 0; turn < MAX_WARM_UP; turn++)
        {
            _unfinished = false;
            scene.Render(dt);

            if (!_unfinished && turn > 0)
                break;

            scene.UpdateState(dt);
            scene.UpdatePhysics(dt);

            // What those updates added is set up before it is drawn or updated, as it is while the game runs
            EntityLifecycle.Flush();

            // It set another scene from its very first update, which is the one to show then
            if (_halt)
                break;
        }

        _unfinished = false;
    }

    /// <summary>
    /// Frees the scenes that were left, with everything they had. What a scene holds is disposed of first, the
    /// way it wants to go, and whatever is left of what it made on the GPU after that is freed for it: not
    /// everything cleans up after itself, and a scene mustn't depend on that to not leak.
    /// </summary>
    private void DisposeRetired()
    {
        Scene[] gone;
        lock (_retired)
        {
            if (_retired.Count == 0) return;

            gone = [.. _retired];
            _retired.Clear();
        }

        foreach (Scene scene in gone)
        {
            try
            {
                scene.Enabled = false;
                scene.Dispose();
            }
            catch (Exception exception)
            {
                Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[SceneManager] '{scene.Name}' didn't go quietly: {exception.Message}");
            }

            int freed = GameEngine.Instance.ObjectManager.Release(scene.Assets);
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[SceneManager] Left '{scene.Assets.Name}', freed the {freed} GPU objects it still had.");
        }
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
