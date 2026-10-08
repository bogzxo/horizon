using Horizon.Content.Disposers;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Finalizers;

/// <summary>
/// Unloads injected render buffers.
/// </summary>
public class RenderBufferObjectFinalizer : IGameAssetFinalizer<RenderBufferObject>
{
    public static void Dispose(in RenderBufferObject asset)
    {
        ObjectManager.GL.DeleteRenderbuffer(asset.Handle);
    }

    public static unsafe void DisposeAll(in IEnumerable<RenderBufferObject> assets)
    {
        if (!assets.Any())
            return;

        // aggregate all handles into an array.
        uint[] handles = assets.Select((t) => t.Handle).ToArray();

        fixed (uint* first = &handles[0])
            ObjectManager.GL.DeleteRenderbuffers((uint)handles.Length, first);
    }
}