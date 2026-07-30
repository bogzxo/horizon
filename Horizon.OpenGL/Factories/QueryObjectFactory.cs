using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Factories;

public class QueryObjectFactory : IAssetFactory<QueryObject, QueryObjectDescription>
{
    public static unsafe bool TryCreate(
        in QueryObjectDescription description,
        out AssetCreationResult<QueryObject> result
    )
    {
        var buffer = new QueryObject
        {
            Handle = ObjectManager.GL.GenQuery(),
            Target = description.Target,
        };

        if (buffer.Handle == 0)
        {
            result = new() { Asset = buffer, Status = AssetCreationStatus.Failed };
            return false;
        }

        result = new()
        {
            Asset = buffer,
            Status = AssetCreationStatus.Success,
            Message = "Query Object created!"
        };
        return true;
    }
}
