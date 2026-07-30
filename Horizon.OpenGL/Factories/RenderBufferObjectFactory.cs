using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Factories;

public class RenderBufferObjectFactory
    : IAssetFactory<RenderBufferObject, RenderBufferObjectDescription>
{
    public static bool TryCreate(
        in RenderBufferObjectDescription description,
        out AssetCreationResult<RenderBufferObject> result
    )
    {
        var asset = new RenderBufferObject
        {
            Handle = ObjectManager.GL.CreateRenderbuffer()
        };

        ObjectManager.GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, asset.Handle);

        if (description.Samples == 0) ObjectManager.GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, description.InternalFormat, description.Width, description.Height);

        else ObjectManager.GL.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, description.Samples, description.InternalFormat, description.Width, description.Height);

        ObjectManager.GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        result = new() { 
            Asset = asset,
            Message = "RenderBuffer created successfully!",
            Status = AssetCreationStatus.Success,
        };
        return true;
    }
}
