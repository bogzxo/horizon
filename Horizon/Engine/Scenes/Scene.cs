using System.Diagnostics.CodeAnalysis;

using Horizon.Content;

namespace Horizon.Engine;

/// <summary>
/// One screen of a game. A menu, a level, a fight. It adds what it is made of in <see cref="Core.Entity.Initialize"/>
/// and is updated and drawn for as long as it is the one the engine shows.
/// <code>
/// internal sealed class TitleScene : Scene
/// {
///     public override void Initialize()
///     {
///         ActiveCamera = AddEntity(new Camera2D(Engine.ViewportSize));
///         base.Initialize();
///     }
///
///     public override void UpdateState(float dt)
///     {
///         base.UpdateState(dt);
///         if (!IsLeaving &amp;&amp; Engine.Input.Keyboard.AnyPressed) GoTo(new GameScene());
///     }
/// }
/// </code>
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public abstract class Scene : GameObject
{
    /// <summary>
    /// The camera the scene is seen through. A scene that never sets one is seen through the camera of the engine.
    /// </summary>
    public virtual Camera ActiveCamera { get; protected set; } = null!;

    /// <summary>
    /// Whether the scene is kept as it is when another one takes its place, to be set again later. A scene that
    /// isn't (which is what a scene is unless it says otherwise) is disposed of when it is left, along with
    /// everything in <see cref="Assets"/>.
    /// </summary>
    public virtual bool Persistent => false;

    /// <summary>
    /// Called when a <see cref="Persistent"/> scene that was left is set again, as it comes back the way it was left
    /// rather than being set up again. Render thread, with the simulation standing still.
    /// </summary>
    public virtual void OnReturned()
    { }

    /// <summary>
    /// What the scene has on the GPU. Everything that is made while it is set up, drawn, or has something added
    /// to it is noted here as the scene's, by whatever in it makes it. It is all freed when the scene is left,
    /// so nothing in a scene has to free anything for the scene not to leak. Assets that are asked for by name
    /// are shared with the other scenes that ask for them, and go with the last one of those.
    /// </summary>
    public AssetScope Assets { get; }

    /// <summary>
    /// How long the scene has been updated for, in seconds. It stands still while the scene does.
    /// </summary>
    public double Time { get; private set; }

    /// <summary>
    /// Whether another scene has been set and this one is only still on screen until the transition has covered it up.
    /// A scene that is leaving should stop listening to its buttons, or somebody mashing one sets the next scene twice.
    /// </summary>
    public bool IsLeaving { get; internal set; }

    /// <summary>
    /// Whether the scene is kept underneath another one that was put on top of it (see
    /// <see cref="Horizon.Engine.Components.SceneManager.PushScene"/>). It is not the one being played while it is, whether it
    /// still gets its updates or not, and keeps its hands off the buttons.
    /// </summary>
    public bool IsSuspended { get; internal set; }

    protected Scene()
    {
        Assets = new AssetScope(GetType().Name);
    }

    /// <summary>
    /// Leaves for another scene, the way the scene manager is set to hand over.
    /// </summary>
    public void GoTo(Scene scene) => Engine.SetScene(scene);

    /// <summary>
    /// Leaves for another scene through a transition of its own, null for a hard cut.
    /// </summary>
    public void GoTo(Scene scene, SceneTransition? transition) => Engine.SetScene(scene, transition);

    /// <summary>
    /// Puts another scene on top of this one, which is kept underneath until that one goes back (see
    /// <see cref="Return()"/>). Null for the transition is a hard cut.
    /// </summary>
    /// <param name="below">What becomes of this scene while it is underneath, see <see cref="Horizon.Engine.Components.SceneBelow"/>.</param>
    public void Open(Scene scene, SceneTransition? transition = null, Horizon.Engine.Components.SceneBelow below = Horizon.Engine.Components.SceneBelow.Suspended) =>
        Engine.SceneManager.PushScene(scene, transition, below);

    /// <summary>Whether there is a scene underneath this one to go back to.</summary>
    public bool CanReturn => Engine.SceneManager.CanPop;

    /// <summary>
    /// Goes back to the scene underneath, which is on screen again as it was left. This one is done with.
    /// </summary>
    /// <returns>False if there is nothing underneath, a screen that can be got to both ways goes somewhere else then.</returns>
    public bool Return(SceneTransition? transition = null) => Engine.SceneManager.PopScene(transition);

    /// <summary>
    /// Called as another scene has been put on top of this one. Render thread, with the simulation standing
    /// still. For letting go of what shouldn't be held while underneath (the mouse, a sound that loops).
    /// </summary>
    protected internal virtual void OnSuspended()
    { }

    /// <summary>
    /// Called as the scene on top is gone and this one is on screen again. Render thread, with the simulation
    /// standing still, so what it changed in the meantime (the options, the size of the window) can be seen to here.
    /// </summary>
    protected internal virtual void OnResumed()
    { }

    public override void UpdateState(float dt)
    {
        Time += dt;
        base.UpdateState(dt);
    }
}
