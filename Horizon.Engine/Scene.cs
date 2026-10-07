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
    /// What the scene has on the GPU. Everything that is made while it is set up, drawn, or has something added
    /// to it is noted here as the scene's, by whatever in it makes it. It is all freed when the scene is left,
    /// so nothing in a scene has to free anything for the scene not to leak. Assets that are asked for by name
    /// are shared with the other scenes that ask for them, and go with the last one of those.
    /// </summary>
    public AssetScope Assets { get; }

    /// <summary>
    /// How long the scene has been updated for, in seconds. It stands still while the scene does.
    /// </summary>
    public float Time { get; private set; }

    /// <summary>
    /// Whether another scene has been set and this one is only still on screen until the transition has covered it up.
    /// A scene that is leaving should stop listening to its buttons, or somebody mashing one sets the next scene twice.
    /// </summary>
    public bool IsLeaving { get; internal set; }

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

    public override void UpdateState(float dt)
    {
        Time += dt;
        base.UpdateState(dt);
    }
}
