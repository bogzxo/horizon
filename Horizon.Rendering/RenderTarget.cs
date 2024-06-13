using System.Numerics;

using Horizon.Core.Components;
using Horizon.Core.Primitives;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

public class RenderTarget : IInstantiable, IRenderable
{
    public FrameBufferObject FrameBuffer { get; protected set; }
    public Vector2 ViewportSize { get; }

    private readonly bool createRenderRectangle = false;
    private readonly bool createFbo;
    private RenderRectangle renderRectangle;

    protected virtual FrameBufferObject CreateFrameBuffer(in uint width, in uint height)
    {
        if (GameEngine
            .Instance
            .ObjectManager
            .FrameBuffers
            .TryCreate(
                new FrameBufferObjectDescription
                {
                    Width = width,
                    Height = height,
                    Attachments = new() {
                        { FramebufferAttachment.ColorAttachment0, FrameBufferAttachmentDefinition.TextureRGBAByte },
                        { FramebufferAttachment.DepthAttachment, FrameBufferAttachmentDefinition.TextureDepth },
                    }
                },
                out var result
            ))
        {
            return result.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
            throw new Exception(result.Message);
        }
    }


    public RenderTarget(in uint width, in uint height)
    {
        ViewportSize = new Vector2(width, height);
    }
    public RenderTarget(in uint width, in uint height, in Technique technique, bool createFbo = true)
        : this(width, height)
    {
        this.createRenderRectangle = true;
        this.renderRectangle = new RenderRectangle(technique);
        this.createFbo = createFbo;
    }

    public void Initialize()
    {
        if (createFbo) FrameBuffer = CreateFrameBuffer((uint)ViewportSize.X, (uint)ViewportSize.Y);
        renderRectangle?.Initialize();
    }

    public void Render(float dt, object? obj = null)
    {
        if (!createRenderRectangle) return;

        renderRectangle.Render(dt, obj);
    }
    public void BindForRendering()
    {
        FrameBuffer.Bind();
        FrameBuffer.Viewport();
    }

    public void BindForReading(in FramebufferAttachment attachment, in uint unit) => FrameBuffer.BindAttachment(attachment, unit);
}