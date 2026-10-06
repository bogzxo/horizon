using Bogz.Logging.Loggers;

using Horizon.Content.Managers;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Factories;
using Horizon.OpenGL.Finalizers;

namespace Horizon.OpenGL.Managers;

/// <summary>
/// Managed class to create, manage and destroy unmanaged OpenGL assets.
/// </summary>
public class ObjectManager : IGameComponent, IDisposable
{
    internal static ObjectManager Instance { get; private set; }

    public static Silk.NET.OpenGL.GL GL { get; private set; }

    public AssetManager<
        Texture,
        TextureFactory,
        TextureDescription,
        TextureFinalizer
    > Textures
    { get; init; }

    public AssetManager<
        Shader,
        ShaderFactory,
        ShaderDescription,
        ShaderFinalizer
    > Shaders
    { get; init; }

    public AssetManager<
        BufferObject,
        BufferObjectFactory,
        BufferObjectDescription,
        BufferObjectFinalizer
    > Buffers
    { get; init; }

    public AssetManager<
       QueryObject,
       QueryObjectFactory,
       QueryObjectDescription,
       QueryObjectFinalizer
   > Queries
    { get; init; }


    public AssetManager<
        RenderBufferObject,
        RenderBufferObjectFactory,
        RenderBufferObjectDescription,
        RenderBufferObjectFinalizer
    > RenderBuffers
    { get; init; }

    public AssetManager<
        FrameBufferObject,
        FrameBufferObjectFactory,
        FrameBufferObjectDescription,
        FrameBufferObjectFinalizer
    > FrameBuffers
    { get; init; }

    public AssetManager<
        VertexArrayObject,
        VertexArrayObjectFactory,
        VertexArrayObjectDescription,
        VertexArrayObjectFinalizer
    > VertexArrays
    { get; init; }

    public string Name { get; set; }
    public Entity Parent { get; set; }
    public bool Enabled { get; set; }

    public ObjectManager()
    {
        Instance = this;
        Name = "Content Manager";

        Textures = new();

        // Kept for good once they are made: compiling one takes far longer than keeping it costs
        Shaders = new() { Scoped = false };
        Queries = new();
        Buffers = new();
        RenderBuffers = new();
        FrameBuffers = new();
        VertexArrays = new();
    }

    public void Initialize()
    {
        GL = Parent.GetComponent<WindowManager>().GL;

        Textures.SetMessageCallback(ConcurrentLogger.Instance.Log);
        Shaders.SetMessageCallback(ConcurrentLogger.Instance.Log);
        Buffers.SetMessageCallback(ConcurrentLogger.Instance.Log);
        VertexArrays.SetMessageCallback(ConcurrentLogger.Instance.Log);
        FrameBuffers.SetMessageCallback(ConcurrentLogger.Instance.Log);
        RenderBuffers.SetMessageCallback(ConcurrentLogger.Instance.Log);
        Queries.SetMessageCallback(ConcurrentLogger.Instance.Log);
    }

    /// <summary>
    /// What existed at one moment, as taken by <see cref="Snapshot"/>.
    /// </summary>
    public sealed class AssetSnapshot
    {
        internal HashSet<uint> Textures = [], Shaders = [], Buffers = [], Queries = [], RenderBuffers = [], FrameBuffers = [], VertexArrays = [];
    }

    /// <summary>
    /// Records which assets exist, so that everything created afterwards can be freed in one go with
    /// <see cref="ReleaseSince"/>. Has to be called on the GL thread.
    /// </summary>
    public AssetSnapshot Snapshot() => new()
    {
        Textures = Textures.GetOwnedHandles(),
        Shaders = Shaders.GetOwnedHandles(),
        Buffers = Buffers.GetOwnedHandles(),
        Queries = Queries.GetOwnedHandles(),
        RenderBuffers = RenderBuffers.GetOwnedHandles(),
        FrameBuffers = FrameBuffers.GetOwnedHandles(),
        VertexArrays = VertexArrays.GetOwnedHandles()
    };

    /// <summary>
    /// Frees every asset created since a snapshot was taken, apart from named ones, which stay cached
    /// for the next user. Has to be called on the GL thread, once nothing draws with those assets any more.
    /// </summary>
    /// <returns>How many assets were freed.</returns>
    public int ReleaseSince(AssetSnapshot snapshot)
    {
        // The same order as Dispose: what refers to something goes before the thing it refers to.
        return FrameBuffers.RemoveUnnamedExcept(snapshot.FrameBuffers)
            + Textures.RemoveUnnamedExcept(snapshot.Textures)
            + RenderBuffers.RemoveUnnamedExcept(snapshot.RenderBuffers)
            + Shaders.RemoveUnnamedExcept(snapshot.Shaders)
            + Queries.RemoveUnnamedExcept(snapshot.Queries)
            + VertexArrays.RemoveUnnamedExcept(snapshot.VertexArrays)
            + Buffers.RemoveUnnamedExcept(snapshot.Buffers);
    }

    /// <summary>
    /// Frees everything a scope has (see <see cref="Horizon.Content.AssetScope"/>): what was made in it, and what
    /// it asked for by name that nobody else uses. Has to be called on the GL thread, once nothing draws with
    /// those assets any more. The scope is done with afterwards.
    /// </summary>
    /// <returns>How many assets were freed.</returns>
    public int Release(Horizon.Content.AssetScope scope)
    {
        scope.MarkReleased();

        // The same order as Dispose: what refers to something goes before the thing it refers to.
        return FrameBuffers.Release(scope)
            + Textures.Release(scope)
            + RenderBuffers.Release(scope)
            + Shaders.Release(scope)
            + Queries.Release(scope)
            + VertexArrays.Release(scope)
            + Buffers.Release(scope);
    }

    public void Render(float dt, object? obj = null)
    { }

    public void UpdateState(float dt)
    { }

    public void UpdatePhysics(float dt)
    { }

    public void Dispose()
    {
        // textures bound to fbo so we free the fbos first.
        FrameBuffers.Dispose();
        Textures.Dispose();
        RenderBuffers.Dispose();

        Shaders.Dispose();
        Queries.Dispose();

        // the vbos are bound to vaos so we need to dispose the vaos first.
        VertexArrays.Dispose();
        Buffers.Dispose();

        GC.SuppressFinalize(this);
    }
}