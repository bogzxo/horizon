using Horizon.Content.Disposers;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Finalizers;

public class QueryObjectFinalizer : IGameAssetFinalizer<QueryObject>
{
    public static void Dispose(in QueryObject asset)
    {
        ObjectManager.GL.DeleteQuery(asset.Handle);
    }

    public static unsafe void DisposeAll(in IEnumerable<QueryObject> assets)
    {
        if (!assets.Any())
            return;

        // aggregate all handles into an array.
        uint[] handles = assets.Select((t) => t.Handle).ToArray();

        fixed (uint* first = &handles[0])
            ObjectManager.GL.DeleteQueries((uint)handles.Length, first);
    }
}
