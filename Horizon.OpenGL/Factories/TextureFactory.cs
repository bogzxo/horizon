using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.OpenGL.Factories;

/// <summary>
/// Asset factory for creating instances of <see cref="Texture"/>.
/// </summary>
public class TextureFactory : IAssetFactory<Texture, TextureDescription>
{
    public static bool TryCreate(
        in TextureDescription description,
        out AssetCreationResult<Texture> result
        )
    {
        if (description.Paths.Length == 0)
            return CreateFromDimensions(description.Width, description.Height, description.Definition, out result);
        if (description.Paths.Length == 6) // Check if cube map paths are provided
            return CreateCubeMap(description.Width, description.Height, description.Paths, description.Definition, out result);
        else if (!string.IsNullOrEmpty(description.Paths[0]))
            return CreateFromFile(description.Paths[0], description.Definition, out result);

        result = new AssetCreationResult<Texture>()
        {
            Asset = Texture.Invalid,
            Status = AssetCreationStatus.Failed,
        };
        return false;
    }

    private static unsafe bool CreateFromFile(
        in string path,
        in TextureDefinition definition,
        out AssetCreationResult<Texture> result
    )
    {
        if (!File.Exists(path))
        {
            result = new AssetCreationResult<Texture>()
            {
                Asset = Texture.Invalid,
                Message = $"Failed to find image '{path}'!",
                Status = AssetCreationStatus.Failed,
            };
            return false;
        }
        using var img = Image.Load<Rgba32>(path);


        var texture = new Texture
        {
            Handle = ObjectManager.GL.CreateTexture(TextureTarget.Texture2D),
            Width = (uint)img.Width,
            Height = (uint)img.Height,
            TextureTarget = definition.TextureTarget,
        };

        ObjectManager.GL.ActiveTexture(TextureUnit.Texture0);
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, texture.Handle);

        // Reserve enough memory from the GPU for the whole image
        ObjectManager.GL.TexImage2D(
            definition.TextureTarget,
            0,
            definition.InternalFormat,
            texture.Width,
            texture.Height,
            0,
            definition.PixelFormat,
            definition.PixelType,
            null
        );

        int y = 0;
        img.ProcessPixelRows(accessor =>
        {
            // ImageSharp 2 does not store images in contiguous memory by default, so we must send the image row by row
            for (; y < accessor.Height; y++)
            {
                fixed (void* data = accessor.GetRowSpan(y))
                {
                    // Loading the actual image.
                    ObjectManager.GL.TexSubImage2D(
                        TextureTarget.Texture2D,
                        0,
                        0,
                        y,
                        (uint)accessor.Width,
                        1,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        data
                    );
                }
            }
        });

        SetParameters(definition);
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, 0);

        result = new() { Asset = texture, Status = AssetCreationStatus.Success };
        return true;
    }

    private static unsafe bool CreateCubeMap(
        uint width, uint height,
        string[] paths,
        in TextureDefinition definition,
        out AssetCreationResult<Texture> result
        )
    {
        // Check if exactly 6 paths are provided
        if (paths.Length != 6)
        {
            result = new AssetCreationResult<Texture>()
            {
                Asset = Texture.Invalid,
                Status = AssetCreationStatus.Failed,
                Message = "A cube map requires exactly 6 image paths."
            };
            return false;
        }

        // Generate cube map texture
        var texture = new Texture
        {
            Handle = ObjectManager.GL.GenTexture(),
            Width = width, // Cube maps have no dimension properties
            Height = height,
            TextureTarget = definition.TextureTarget,
        };

        ObjectManager.GL.ActiveTexture(TextureUnit.Texture0);
        ObjectManager.GL.BindTexture(TextureTarget.TextureCubeMap, texture.Handle);

        // Load each face of the cube map
        for (int i = 0; i < 6; i++)
        {
            using var img = Image.Load<Rgba32>(paths[i]);

            ObjectManager.GL.TexImage2D(
                  (TextureTarget.TextureCubeMapPositiveX + i),
                  0,
                  definition.InternalFormat,
                  texture.Width,
                  texture.Height,
                  0,
                  definition.PixelFormat,
                  definition.PixelType,
                  null
              );

            int y = 0;
            img.ProcessPixelRows(accessor =>
            {
                // ImageSharp 2 does not store images in contiguous memory by default, so we must send the image row by row
                for (; y < accessor.Height; y++)
                {
                    fixed (void* data = accessor.GetRowSpan(y))
                    {
                        // Loading the actual image.
                        ObjectManager.GL.TexSubImage2D(
                            TextureTarget.TextureCubeMapPositiveX + i,
                            0,
                            0,
                            y,
                            (uint)accessor.Width,
                            1,
                            PixelFormat.Rgba,
                            PixelType.UnsignedByte,
                            data
                        );
                    }
                }
            });
        }

        SetParameters(definition);
        ObjectManager.GL.BindTexture(TextureTarget.TextureCubeMap, 0);

        result = new AssetCreationResult<Texture>
        {
            Asset = texture,
            Status = AssetCreationStatus.Success
        };
        return true;
    }
    private static unsafe bool CreateFromDimensions(
 in uint width,
 in uint height,
 in TextureDefinition definition,
 out AssetCreationResult<Texture> result
)
    {
        ObjectManager.GL.GetError(); // Clear errors
        var texture = new Texture
        {
            Handle = ObjectManager.GL.GenTexture(),
            Width = (uint)width,
            Height = (uint)height,
            TextureTarget = definition.TextureTarget,
        };

        // Check if texture creation was successful
        if (texture.Handle == 0)
        {
            result = new AssetCreationResult<Texture>
            {
                Status = AssetCreationStatus.Failed,
                Message = "Failed to generate texture handle"
            };
            return false;
        }

        ObjectManager.GL.ActiveTexture(TextureUnit.Texture0);
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, texture.Handle);

        // Reserve enough memory from the GPU for the whole image
        ObjectManager.GL.TexImage2D(
            definition.TextureTarget,
            0,
            definition.InternalFormat,
            texture.Width,
            texture.Height,
            0,
            definition.PixelFormat,
            definition.PixelType,
            null
        );

        // Check for OpenGL errors after TexImage2D call
        var error = ObjectManager.GL.GetError();
        if (error != GLEnum.NoError)
        {
            result = new AssetCreationResult<Texture>
            {
                Status = AssetCreationStatus.Failed,
                Message = $"Failed to allocate texture memory: {error}"
            };
            return false;
        }

        // Set texture parameters
        SetParameters(definition);

        // Unbind texture
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, 0);

        // Check for OpenGL errors after unbinding texture
        error = ObjectManager.GL.GetError();
        if (error != GLEnum.NoError)
        {
            result = new AssetCreationResult<Texture>
            {
                Status = AssetCreationStatus.Failed,
                Message = $"Failed to unbind texture: {error}"
            };
            return false;
        }

        result = new AssetCreationResult<Texture>
        {
            Asset = texture,
            Status = AssetCreationStatus.Success
        };
        return true;
    }
    private static void SetParameters(in TextureDefinition definition)
    {
        foreach (var param in definition.Parameters)
        {
            ObjectManager
            .GL
            .TexParameter(
                definition.TextureTarget,
                param.Name,
                param.Value
            );
        }
        //Generating mipmaps.
        ObjectManager.GL.GenerateMipmap(definition.TextureTarget);
    }
}