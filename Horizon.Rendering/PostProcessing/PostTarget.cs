using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// A picture to draw into and read back: what the passes of the post processing hand to each other. One texture of
/// colour and nothing else, in whatever format and with whatever filter it is asked for.
/// </summary>
public sealed class PostTarget : IDisposable
{
    /// <summary>Colours, blended between the pixels when read at another size. What a picture is usually kept in.</summary>
    public static TextureDefinition Smooth => TextureDefinition.RgbaUnsignedByte;

    /// <summary>Read back exactly as written, for what holds data rather than a picture.</summary>
    public static TextureDefinition Exact => TextureDefinition.RgbaUnsignedByteNearest;

    /// <summary>
    /// Colours that are taken to be in sRGB and handed to whoever reads them in linear light, blended between the
    /// pixels after that: the GPU does the conversion and gets the blending right, which done by hand in a shader
    /// is a power a channel for every read. What is drawn into it goes in as it is. Black outside of its edges.
    /// </summary>
    public static TextureDefinition LinearLight { get; } = new()
    {
        InternalFormat = InternalFormat.Srgb8Alpha8,
        PixelFormat = PixelFormat.Rgba,
        PixelType = PixelType.UnsignedByte,
        TextureTarget = TextureTarget.Texture2D,
        Parameters =
        [
            new() { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToBorder },
            new() { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToBorder },
            new() { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
            new() { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },
            new() { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
            new() { Name = TextureParameterName.TextureMaxLevel, Value = 0 }
        ]
    };

    private readonly FrameBufferObject frameBuffer;

    public Texture Texture { get; }

    /// <summary>The size in pixels.</summary>
    public Vector2 Size { get; }

    /// <summary>Has to be made on the GL thread.</summary>
    /// <param name="definition">What kind of texture it is, see the ones this class offers. <see cref="Smooth"/> if left out.</param>
    /// <exception cref="Exception">The GPU wouldn't have it.</exception>
    public PostTarget(uint width, uint height, TextureDefinition? definition = null)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        bool created = GameEngine.Instance.ObjectManager.FrameBuffers.TryCreate(
            new FrameBufferObjectDescription
            {
                Width = width,
                Height = height,
                Attachments = new()
                {
                    {
                        FramebufferAttachment.ColorAttachment0,
                        new FrameBufferAttachmentDefinition { IsRenderBuffer = false, TextureDefinition = definition ?? Smooth }
                    }
                }
            },
            out var result);

        if (!created)
            throw new Exception($"A post processing target of {width} by {height} couldn't be made: {result.Message}");

        frameBuffer = result.Asset;
        Texture = frameBuffer.Color;
        Size = new Vector2(width, height);
    }

    /// <summary>Whether this is the size somebody wants, who would otherwise make another.</summary>
    public bool Fits(uint width, uint height) => (uint)Size.X == Math.Max(1, width) && (uint)Size.Y == Math.Max(1, height);

    /// <summary>Makes this what is drawn into, all of it.</summary>
    public void Bind()
    {
        frameBuffer.Bind();
        frameBuffer.Viewport();
    }

    /// <summary>
    /// Copies what is in the window right now into this target, which is the frame as far as it has been drawn.
    /// It is stretched to fit if the two aren't the same size. GL thread, and whatever is bound stays bound.
    /// </summary>
    public void CopyFromWindow()
    {
        var engine = GameEngine.Instance;
        Vector2 window = engine.WindowManager.ViewportSize;

        // Zero is the window, the one frame buffer nobody had to make
        engine.GL.BlitNamedFramebuffer(
            0, frameBuffer.Handle,
            0, 0, (int)window.X, (int)window.Y,
            0, 0, (int)Size.X, (int)Size.Y,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Linear);
    }

    public void Dispose() => frameBuffer.Dispose();
}
