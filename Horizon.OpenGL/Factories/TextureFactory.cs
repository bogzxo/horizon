﻿using Horizon.Content;
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
    public static AssetCreationResult<Texture> Create(in TextureDescription description)
    {
        if (description.Path.CompareTo(string.Empty) != 0)
            return CreateFromFile(description.Path, description.Definition);
        if (description.Width + description.Height > 2)
            return CreateFromDimensions(
                description.Width,
                description.Height,
                description.Definition
            );

        return new AssetCreationResult<Texture>()
        {
            Asset = Texture.Invalid,
            Status = AssetCreationStatus.Failed,
        };
    }

    private static unsafe AssetCreationResult<Texture> CreateFromDimensions(
    in uint width,
    in uint height,
    in TextureDefinition definition
)
    {
        ObjectManager.GL.GetError(); // Clear errors
        var texture = new Texture
        {
            Handle = ObjectManager.GL.GenTexture(),
            Width = (uint)width,
            Height = (uint)height
        };

        // Check if texture creation was successful
        if (texture.Handle == 0)
        {
            return new AssetCreationResult<Texture>
            {
                Status = AssetCreationStatus.Failed,
                Message = "Failed to generate texture handle"
            };
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
            return new AssetCreationResult<Texture>
            {
                Status = AssetCreationStatus.Failed,
                Message = $"Failed to allocate texture memory: {error}"
            };
        }

        // Set texture parameters
        SetParameters();

        // Unbind texture
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, 0);

        // Check for OpenGL errors after unbinding texture
        error = ObjectManager.GL.GetError();
        if (error != GLEnum.NoError)
        {
            return new AssetCreationResult<Texture>
            {
                Status = AssetCreationStatus.Failed,
                Message = $"Failed to unbind texture: {error}"
            };
        }

        return new AssetCreationResult<Texture>
        {
            Asset = texture,
            Status = AssetCreationStatus.Success
        };
    }

    private static unsafe AssetCreationResult<Texture> CreateFromFile(
        in string path,
        in TextureDefinition definition
    )
    {
        using var img = Image.Load<Rgba32>(path);

        var texture = new Texture
        {
            Handle = ObjectManager.GL.CreateTexture(TextureTarget.Texture2D),
            Width = (uint)img.Width,
            Height = (uint)img.Height
        };

        ObjectManager.GL.ActiveTexture(TextureUnit.Texture0);
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, texture.Handle);

        //Reserve enough memory from the gpu for the whole image
        ObjectManager
            .GL
            .TexImage2D(
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
            //ImageSharp 2 does not store images in contiguous memory by default, so we must send the image row by row :cry:
            for (; y < accessor.Height; y++)
            {
                fixed (void* data = accessor.GetRowSpan(y))
                {
                    //Loading the actual image.
                    ObjectManager
                        .GL
                        .TexSubImage2D(
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
        SetParameters();
        ObjectManager.GL.BindTexture(TextureTarget.Texture2D, 0);

        return new() { Asset = texture, Status = AssetCreationStatus.Success };
    }

    private static void SetParameters()
    {
        // Setting some texture parameters so the texture behaves as expected.
        ObjectManager
            .GL
            .TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)GLEnum.ClampToEdge
            );
        ObjectManager
            .GL
            .TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)GLEnum.ClampToEdge
            );
        ObjectManager
            .GL
            .TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)GLEnum.NearestMipmapLinear
            );
        ObjectManager
            .GL
            .TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)GLEnum.Nearest
            );
        ObjectManager.GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        ObjectManager.GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        //Generating mipmaps.
        ObjectManager.GL.GenerateMipmap(TextureTarget.Texture2D);
    }
}