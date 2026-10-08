using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.Content.Disposers;
using Horizon.Content.Managers;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Logging;

namespace Horizon.Graphics;

/// <summary>Makes an asset by calling its own TryCreate, for the asset managers.</summary>
public sealed class TextureFactory : IAssetFactory<Texture, TextureDescription>
{
    public static bool TryCreate(in TextureDescription description, out AssetCreationResult<Texture> result) => Texture.TryCreate(description, out result);
}

public sealed class ShaderFactory : IAssetFactory<Shader, ShaderDescription>
{
    public static bool TryCreate(in ShaderDescription description, out AssetCreationResult<Shader> result) => Shader.TryCreate(description, out result);
}

public sealed class BufferFactory : IAssetFactory<GpuBuffer, BufferDescription>
{
    public static bool TryCreate(in BufferDescription description, out AssetCreationResult<GpuBuffer> result)
    {
        try
        {
            result = new AssetCreationResult<GpuBuffer> { Asset = new GpuBuffer(GraphicsDevice.Current, description), Status = AssetCreationStatus.Success, Message = string.Empty };
            return true;
        }
        catch (Exception e)
        {
            result = new AssetCreationResult<GpuBuffer> { Status = AssetCreationStatus.Failed, Message = e.Message };
            return false;
        }
    }
}

public sealed class RenderTargetFactory : IAssetFactory<RenderTarget, RenderTargetDescription>
{
    public static bool TryCreate(in RenderTargetDescription description, out AssetCreationResult<RenderTarget> result) => RenderTarget.TryCreate(description, out result);
}

public sealed class VertexArrayFactory : IAssetFactory<VertexArray, VertexArrayDescription>
{
    public static bool TryCreate(in VertexArrayDescription description, out AssetCreationResult<VertexArray> result) => VertexArray.TryCreate(description, out result);
}

/// <summary>Frees GPU resources, one or many, for the asset managers.</summary>
public sealed class GpuFinalizer<T> : IGameAssetFinalizer<T> where T : GpuResource
{
    public static void Dispose(in T asset) => asset.Destroy();

    public static void DisposeAll(in IEnumerable<T> assets)
    {
        foreach (var asset in assets) asset.Destroy();
    }
}

/// <summary>
/// Keeps track of everything that is made on the GPU, who made it (see <see cref="AssetScope"/>), what is shared by
/// name, and freeing it all when a scene goes. Every resource class makes its things through here.
/// </summary>
public class ObjectManager : GameComponent, IDisposable
{
    internal static ObjectManager Instance { get; private set; } = null!;

    public AssetManager<Texture, TextureFactory, TextureDescription, GpuFinalizer<Texture>> Textures { get; init; }
    public AssetManager<Shader, ShaderFactory, ShaderDescription, GpuFinalizer<Shader>> Shaders { get; init; }
    public AssetManager<GpuBuffer, BufferFactory, BufferDescription, GpuFinalizer<GpuBuffer>> Buffers { get; init; }
    public AssetManager<RenderTarget, RenderTargetFactory, RenderTargetDescription, GpuFinalizer<RenderTarget>> RenderTargets { get; init; }
    public AssetManager<VertexArray, VertexArrayFactory, VertexArrayDescription, GpuFinalizer<VertexArray>> VertexArrays { get; init; }

    public ObjectManager()
    {
        Instance = this;
        Name = "Content Manager";

        Textures = new();

        // Kept for good once they are made, compiling one takes far longer than keeping it costs
        Shaders = new() { Scoped = false };
        Buffers = new();
        RenderTargets = new();
        VertexArrays = new();
    }

    public override void Initialize()
    {
        Textures.SetMessageCallback(Log.Write);
        Shaders.SetMessageCallback(Log.Write);
        Buffers.SetMessageCallback(Log.Write);
        VertexArrays.SetMessageCallback(Log.Write);
        RenderTargets.SetMessageCallback(Log.Write);
    }

    /// <summary>What existed at one moment, as taken by <see cref="Snapshot"/>.</summary>
    public sealed class AssetSnapshot
    {
        internal HashSet<uint> Textures = [], Shaders = [], Buffers = [], RenderTargets = [], VertexArrays = [];
    }

    /// <summary>
    /// Records which assets exist, so that everything created afterwards can be freed in one go with
    /// <see cref="ReleaseSince"/>. Render thread.
    /// </summary>
    public AssetSnapshot Snapshot() => new()
    {
        Textures = Textures.GetOwnedHandles(),
        Shaders = Shaders.GetOwnedHandles(),
        Buffers = Buffers.GetOwnedHandles(),
        RenderTargets = RenderTargets.GetOwnedHandles(),
        VertexArrays = VertexArrays.GetOwnedHandles()
    };

    /// <summary>
    /// Frees every asset created since a snapshot was taken, apart from named ones, which stay cached
    /// for the next user. Render thread, once nothing draws with those assets any more.
    /// </summary>
    /// <returns>How many assets were freed.</returns>
    public int ReleaseSince(AssetSnapshot snapshot)
    {
        // The same order as Dispose, what refers to something goes before the thing it refers to
        return RenderTargets.RemoveUnnamedExcept(snapshot.RenderTargets)
            + Textures.RemoveUnnamedExcept(snapshot.Textures)
            + Shaders.RemoveUnnamedExcept(snapshot.Shaders)
            + VertexArrays.RemoveUnnamedExcept(snapshot.VertexArrays)
            + Buffers.RemoveUnnamedExcept(snapshot.Buffers);
    }

    /// <summary>
    /// Frees everything a scope has (see <see cref="AssetScope"/>), what was made in it, and what it asked for by
    /// name that nobody else uses. Render thread, once nothing draws with those assets any more.
    /// </summary>
    /// <returns>How many assets were freed.</returns>
    public int Release(AssetScope scope)
    {
        scope.MarkReleased();

        return RenderTargets.Release(scope)
            + Textures.Release(scope)
            + Shaders.Release(scope)
            + VertexArrays.Release(scope)
            + Buffers.Release(scope);
    }

    public void Dispose()
    {
        // Textures are attached to render targets, so those go first; buffers are read by vertex arrays, so those go last
        RenderTargets.Dispose();
        Textures.Dispose();
        Shaders.Dispose();
        VertexArrays.Dispose();
        Buffers.Dispose();

        GC.SuppressFinalize(this);
    }
}
