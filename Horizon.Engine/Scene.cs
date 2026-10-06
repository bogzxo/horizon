using System.Diagnostics.CodeAnalysis;

using Horizon.Content;

namespace Horizon.Engine;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public abstract class Scene : GameObject
{
    public abstract Camera ActiveCamera { get; protected set; }

    /// <summary>
    /// Whether the scene is kept as it is when another one takes its place, to be set again later. A scene that
    /// isn't (which is what a scene is unless it says otherwise) is disposed of when it is left, along with
    /// everything in <see cref="Assets"/>.
    /// </summary>
    public virtual bool Persistent => false;

    /// <summary>
    /// What the scene has on the GPU: everything that is made while it is set up, drawn, or has something added
    /// to it is noted here as the scene's, by whatever in it makes it. It is all freed when the scene is left,
    /// so nothing in a scene has to free anything for the scene not to leak. Assets that are asked for by name
    /// are shared with the other scenes that ask for them, and go with the last one of those.
    /// </summary>
    public AssetScope Assets { get; }

    protected Scene()
    {
        Assets = new AssetScope(GetType().Name);
    }
}
