using Bogz.Logging;
using System;
using System.Collections.Generic;
using System.Threading;

using Horizon.Core;

using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Core.Tweening;

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
/// <para>
/// A swap doesn't have to be a hard cut. Give the manager a <see cref="Transition"/> and every scene change covers the
/// old scene up first, swaps while nobody can see and uncovers the new one. See <see cref="SceneTransition"/>.
/// </para>
/// </summary>
public class SceneManager : Entity
{
    // The most turns a scene that was just set gets to finish itself before it is shown as it is
    private const int MAX_WARM_UP = 8;

    // The longest step a transition takes in one frame. Setting a scene up can take a while, and the fade shouldn't skip to its end because of it
    private const float MAX_TRANSITION_STEP = 1.0f / 30.0f;

    // Only one tween moves the cover at a time, covering up takes over from uncovering and the other way round
    private const string COVER_CHANNEL = "cover";

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

    // How long the last scene took to be made and to be warmed up, for the log
    private double _setUpMs, _warmUpMs;

    // The transition that is covering or uncovering the screen right now, and the one the waiting scene comes in with
    private readonly TweenContext _tweens = new();
    private SceneTransition? _running, _incomingTransition;

    // A scene that is to be set up ahead of being shown at the next chance, and one that has been: set up, warmed up
    // and waiting, so the swap to it costs nothing (see Preload)
    private Scene? _toPreload, _preloaded;

    // The transition that drew over the last frame, which is how it is known when one has stopped
    private SceneTransition? _drawn;

    // How much of the screen is covered, 0 is none of it and 1 is all of it. And whether it is on its way to all of it
    private float _cover;
    private volatile bool _covering;

    // Whether the scene under the cover is the new one already, and whether the old one is on its very last frame
    private bool _arriving;
    private volatile bool _lastFrame;

    /// <summary>
    /// The transition every scene change goes through, unless it is handed one of its own.
    /// Null (which is what it is until somebody sets one) for a hard cut.
    /// </summary>
    public SceneTransition? Transition { get; set; }

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
    /// Has a new scene of a kind take over from the one that is on screen.
    /// </summary>
    public void SetScene<TScene>() where TScene : Scene, new()
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
    /// Has a scene take over from the one that is on screen, through the <see cref="Transition"/> if there is one. From any thread.
    /// </summary>
    public void SetScene(Scene scene) => SetScene(scene, Transition);

    /// <summary>
    /// Has a scene take over from the one that is on screen. From any thread.
    /// Without a transition that happens at the start of the next frame, and the scene that is left is not updated any more.
    /// With one the scene that is left stays on screen (and keeps getting updated) for as long as it takes to cover it up,
    /// so a scene that has set another one should stop listening to its buttons.
    /// </summary>
    /// <param name="transition">How this one change is made, null for a hard cut whatever <see cref="Transition"/> says.</param>
    public void SetScene(Scene scene, SceneTransition? transition)
    {
        lock (_changeLock)
        {
            // Whoever is on screen is on their way out from here on, and can tell
            if (CurrentInstance is { } leaving && !ReferenceEquals(leaving, scene))
                leaving.IsLeaving = true;

            // One that was set and never got as far as being shown
            if (_incoming is not null && !ReferenceEquals(_incoming, scene) && !_incoming.Persistent)
            {
                lock (_retired) _retired.Add(_incoming);
            }

            _incoming = scene;
            _incomingTransition = transition;

            // Set up ahead for a scene that isn't the one coming after all
            RetirePreloadedUnless(scene);

            // Nothing to cover up, the scene takes over at the next frame
            if (transition is null || CurrentInstance is null)
            {
                Halt();
                return;
            }

            // Already on the way to covered, the scene that was just set simply takes the place of the one that was waiting
            if (_covering) return;

            _covering = true;
            _arriving = false;
            _running = transition;

            // Set up now, while the old scene is covered up, rather than at the moment it is all the way covered: the
            // wait (loading a scene can take a good while) is when the button was pressed, and the transition itself
            // plays out smoothly with nothing left to do at its height but swap
            QueuePreload(scene);

            // Covered all the way is not the moment to swap yet. The old scene is drawn once more like that first,
            // which is the frame a transition gets to remember it by
            _tweens.Play(
                Tween.To(() => _cover, cover => _cover = cover, 1.0f, MathF.Max(0.0f, transition.OutTime))
                    .SetEasing(transition.OutEasing)
                    .OnComplete(() => _lastFrame = true),
                COVER_CHANNEL);
        }
    }

    /// <summary>
    /// For whatever is drawn in a scene and finds it can't show all of itself yet: a UI that hasn't been updated
    /// once, art that is still on its way to the GPU. Called while drawing, on the render thread. A scene that
    /// has just been set is not shown while anything in it says this, it is given another turn instead (up to a
    /// point). At any other time it does nothing.
    /// </summary>
    public void ReportUnfinished() => _unfinished = true;

    /// <summary>
    /// Sets a scene up ahead of it being shown (made, its assets loaded, warmed up), at the start of the next frame,
    /// so that setting it later swaps to it straight away. For a scene that is pretty sure to come next: the fight
    /// while the map is being picked, the next level while this one is being played. From any thread.
    /// <para>
    /// One scene is kept like that at a time: preloading another, or setting a scene that isn't it, lets go of it
    /// (unless it is <see cref="Scene.Persistent"/>). Setting a scene with a transition preloads it by itself.
    /// </para>
    /// </summary>
    public void Preload(Scene scene)
    {
        lock (_changeLock)
        {
            RetirePreloadedUnless(scene);
            QueuePreload(scene);
        }
    }

    // Helper method to have a scene set up at the next chance, unless it is on screen or set up already. With the lock held
    private void QueuePreload(Scene scene)
    {
        if (ReferenceEquals(scene, CurrentInstance) || ReferenceEquals(scene, _preloaded))
            return;

        _toPreload = scene;
        GameEngine.Instance?.WindowManager.RequestExclusive();
    }

    // Helper method to let go of whatever was set up ahead that isn't a scene, with the lock held
    private void RetirePreloadedUnless(Scene scene)
    {
        if (_toPreload is not null && !ReferenceEquals(_toPreload, scene))
            _toPreload = null;

        if (_preloaded is not { } kept || ReferenceEquals(kept, scene))
            return;

        _preloaded = null;
        if (!kept.Persistent)
        {
            lock (_retired) _retired.Add(kept);
        }
    }

    /// <summary>
    /// Helper method to set up the scene that is to be preloaded. Render thread, at the start of a frame, with the
    /// simulation standing still.
    /// </summary>
    private void PreloadNow(float dt)
    {
        Scene? scene;
        lock (_changeLock)
        {
            scene = _toPreload;
            _toPreload = null;
        }

        if (scene is null || ReferenceEquals(scene, CurrentInstance) || ReferenceEquals(scene, _preloaded))
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int turns;
        using (scene.Assets.Enter())
            turns = SetUp(scene, dt);

        lock (_changeLock)
        {
            // Swapped for another while it was being set up, it goes with the rest of what was left
            if (_incoming is not null && !ReferenceEquals(_incoming, scene) && !scene.Persistent)
            {
                lock (_retired) _retired.Add(scene);
            }
            else
            {
                _preloaded = scene;
            }
        }

        Log.Info($"[SceneManager] Set up '{(scene.Name.Length > 0 ? scene.Name : scene.GetType().Name)}' ahead in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms: {_setUpMs:0} ms making it, {_warmUpMs:0} ms warming it up ({turns} turns).");

        // What the warm up drew is not for showing
        var gl = GameEngine.Instance.GL;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
    }

    /// <summary>
    /// Helper method to stop updating the scene that is on screen and have the one that was set take over, which
    /// happens at the start of the next frame with the simulation standing still (see <see cref="WindowManager.Exclusive"/>).
    /// </summary>
    private void Halt()
    {
        _halt = true;
        GameEngine.Instance?.WindowManager.RequestExclusive();
    }

    public override void Initialize()
    {
        base.Initialize();

        // Swapping scenes sets one up and frees the other, which only the render thread can do and nobody else may watch
        GameEngine.Instance.WindowManager.Exclusive += dt =>
        {
            if (_toPreload is not null)
                PreloadNow(dt);

            if (_halt)
                Change(dt);
        };
    }

    public override void Capture()
    {
        // The scene is not a child of ours, it is updated and drawn by hand, and published the same way
        if (CurrentInstance is { Enabled: true } scene)
            scene.Capture();

        base.Capture();
    }

    public override void Render(float dt)
    {
        // The cover is moved along here rather than with the updates, those stop while a scene is being swapped
        _tweens.Tick(MathF.Min(dt, MAX_TRANSITION_STEP));

        // Drawn alongside the simulation, the scene is drawn once it has been published: not the frame it was swapped in
        // on, before it has had a tick, but every one after
        RenderFrame frame = RenderFrame.Active;
        if (CurrentInstance is { IsDisposed: false } scene && (!frame.IsDecoupled || scene.WasCaptured(frame)))
        {
            // Whatever is made while the scene is drawn (most things are only made when they are first needed)
            // is the scene's
            using (scene.Assets.Enter())
                scene.Render(dt);
        }

        base.Render(dt);

        // Whoever drew over the last frame and doesn't any more is done, and is told so it can let go of what it kept
        SceneTransition? drawing = _cover > 0.0f ? _running : null;
        if (!ReferenceEquals(_drawn, drawing))
        {
            _drawn?.Finish();
            _drawn = drawing;
        }

        // Over everything, whatever the scene drew last
        drawing?.Render(_cover, _arriving, dt);

        // That was the last frame of the old scene, the new one takes over at the start of the next
        if (_lastFrame)
        {
            _lastFrame = false;
            Halt();
        }
    }

    /// <summary>
    /// Helper method to take the cover off again once the scene under it has been swapped, the way the transition
    /// the scene came in with wants it. Without one the cover is simply gone.
    /// </summary>
    private void Uncover(SceneTransition? transition)
    {
        _covering = false;
        _lastFrame = false;

        if (transition is null)
        {
            _tweens.Kill(COVER_CHANNEL);
            _cover = 0.0f;
            _running = null;
            return;
        }

        // From all the way covered, also for a scene that had nothing before it to cover up (the first one)
        _running = transition;
        _arriving = true;
        _cover = 1.0f;
        _tweens.Play(
            Tween.To(() => _cover, cover => _cover = cover, 0.0f, MathF.Max(0.0f, transition.InTime)).SetEasing(transition.InEasing),
            COVER_CHANNEL);
    }

    /// <summary>
    /// Helper method to swap the scene on screen for the one that was set. Render thread, at the start of a frame,
    /// with the simulation standing still (see <see cref="WindowManager.Exclusive"/>).
    /// </summary>
    private void Change(float dt)
    {
        Scene? incoming;
        SceneTransition? transition;
        lock (_changeLock)
        {
            incoming = _incoming;
            transition = _incomingTransition;
            _incoming = null;
            _incomingTransition = null;
            _halt = false;
        }

        Scene? left = CurrentInstance;
        if (ReferenceEquals(incoming, left))
        {
            Uncover(transition);
            DisposeRetired();
            return;
        }

        CurrentInstance = incoming;

        if (incoming is not null)
        {
            // One that was kept and is back has been left before
            incoming.IsLeaving = false;

            bool ready;
            lock (_changeLock)
            {
                ready = ReferenceEquals(_preloaded, incoming);
                if (ready) _preloaded = null;
            }

            // Set up ahead (see Preload), there is nothing left to do but show it
            if (!ready)
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                int turns;
                using (incoming.Assets.Enter())
                    turns = SetUp(incoming, dt);

                Log.Info($"[SceneManager] Set up '{(incoming.Name.Length > 0 ? incoming.Name : incoming.GetType().Name)}' in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms: {_setUpMs:0} ms making it, {_warmUpMs:0} ms warming it up ({turns} turns).");

                // What the warm up drew is not for showing
                var gl = GameEngine.Instance.GL;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            }
        }

        // The scene that is left goes last, once the new one has everything it needs: what the two share by name
        // stays where it is that way, rather than being freed and loaded all over again
        if (left is { Persistent: false })
        {
            lock (_retired) _retired.Add(left);
        }

        DisposeRetired();

        // Last, so the time all of the above took is not time the new scene spends half uncovered
        Uncover(incoming is null ? null : transition);
    }

    /// <summary>
    /// Helper method to set a scene up until it is ready to be shown, with everything it makes noted as its own.
    /// </summary>
    /// <returns>How many turns it took to warm the scene up.</returns>
    private int SetUp(Scene scene, float dt)
    {
        scene.Enabled = false;
        scene.Parent = Parent; // pass through the engine.

        // The scene, and then everything it added while it was at it, all the way down
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        scene.Initialize();
        scene.InitializeAll();
        scene.Enabled = true;
        _setUpMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Everything in the scene exists now, but a scene isn't whole the moment it does. A lot is only made on
        // the GPU the first time it is drawn, a UI has nothing to draw before it has been updated, and art that
        // is asked for while painting arrives a frame later. Shown right away that is a few frames of a
        // background without its UI. So it is drawn and updated here, unseen, until nothing in it says it is
        // still missing something: all of this ends up in one frame, which is drawn over afterwards
        int turn = 0;
        for (; turn < MAX_WARM_UP; turn++)
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
        _warmUpMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return Math.Min(turn + 1, MAX_WARM_UP);
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
                Log.Error($"[SceneManager] '{scene.Name}' didn't go quietly: {exception.Message}");
            }

            int freed = GameEngine.Instance.ObjectManager.Release(scene.Assets);
            Log.Info($"[SceneManager] Left '{scene.Assets.Name}', freed the {freed} GPU objects it still had.");
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
