using System.Numerics;

using Horizon.Core;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

class ZSortingTechnique(ref readonly FrameBufferObject zFbo) // gotta love modern cs, also trying new ref readonly
    : Technique(GameEngine.Instance.ObjectManager.Shaders.CreateOrGet("sprite_zbuff", ShaderDescription.FromPath("shaders/renderer2d", "z_sorting")))
{
    private readonly FrameBufferObject fbo = zFbo;

    private const string U_BG_ALBEDO = "uBackgroundAlbedo";
    private const string U_BG_ZBUFF = "uBackgroundZ";

    private const string U_FG_ALBEDO = "uForegroundAlbedo";
    private const string U_FG_ZBUFF = "uForegroundZ";

    protected override void SetUniforms()
    {
        GameEngine.Instance.GL.BindTextureUnit(0, fbo[FramebufferAttachment.ColorAttachment0]);
        SetUniform(U_BG_ALBEDO, 0); // background albedo
        GameEngine.Instance.GL.BindTextureUnit(1, fbo[FramebufferAttachment.ColorAttachment1]);
        SetUniform(U_BG_ZBUFF, 1); // background z buffer

        GameEngine.Instance.GL.BindTextureUnit(2, fbo[FramebufferAttachment.ColorAttachment2]);
        SetUniform(U_FG_ALBEDO, 2); // foreground albedo
        GameEngine.Instance.GL.BindTextureUnit(3, fbo[FramebufferAttachment.ColorAttachment3]);
        SetUniform(U_FG_ZBUFF, 3); // foreground z buffer
    }
}

/// <summary>
/// Class providing a rendering and post processing pipeline for 2D sprite oriented rendering, specializing in extra functionality for pixel art.
/// "Please be advised that due to poor design you can only instantiate this object with an active GL instance, ie. inside an IInitialize.Initialize() function." - here for historic reasons, no longer the case dw i guess.
/// </summary>
public class Renderer2D : GameObject
{
    public FrameBufferObject FrameBuffer { get => frameBuffer; private set => frameBuffer = value; }
    public RenderRectangle RenderRectangle { get => renderRectangle; private set => renderRectangle = value; }
    public Vector2 ViewportSize { get; init; }

    protected virtual Renderer2DTechnique CreateTechnique() => new(FrameBuffer);

    protected virtual FrameBufferObject CreateFrameBuffer(in uint width, in uint height) => GameEngine
            .Instance
            .ObjectManager
            .FrameBuffers
            .Create(
                new FrameBufferObjectDescription
                {
                    Width = width,
                    Height = height,
                    Attachments = new() {
                        { FramebufferAttachment.ColorAttachment0, OpenGL.Descriptions.TextureDefinition.RgbaFloat },
                    }
                }
            )
            .Asset;

    private FrameBufferObject frameBuffer, backgroundZBuffer;
    private RenderRectangle renderRectangle;
    private ZSortingTechnique zsortingTechnique;
    private Renderer2DTechnique technique;

    public Renderer2D(in uint width, in uint height)
    {
        ViewportSize = new Vector2(width, height);
    }

    public override void Initialize()
    {
        base.Initialize();

        // i am aware we just went from uint -> float!! -> uint but fuck it we ball.
        FrameBuffer = CreateFrameBuffer((uint)ViewportSize.X, (uint)ViewportSize.Y);

        backgroundZBuffer = GameEngine.Instance.ObjectManager.FrameBuffers.Create(new FrameBufferObjectDescription
        {
            Attachments = new() {
                { FramebufferAttachment.ColorAttachment0, TextureDefinition.RgbaFloat }, // background 
                { FramebufferAttachment.ColorAttachment1, new() { // background Z buffer
                    InternalFormat = InternalFormat.R32ui,
                    PixelFormat = PixelFormat.RedInteger,
                    PixelType = PixelType.UnsignedInt,
                    TextureTarget = TextureTarget.Texture2D
                } },
                 { FramebufferAttachment.ColorAttachment2, TextureDefinition.RgbaFloat }, // foreground 
                { FramebufferAttachment.ColorAttachment3, new() { // foreground Z buffer
                    InternalFormat = InternalFormat.R32ui,
                    PixelFormat = PixelFormat.RedInteger,
                    PixelType = PixelType.UnsignedInt,
                    TextureTarget = TextureTarget.Texture2D
                } },
            },
            Width = (uint)ViewportSize.X,
            Height = (uint)ViewportSize.Y,
        }).Asset;

        zsortingTechnique = new ZSortingTechnique(ref backgroundZBuffer);
        technique = CreateTechnique();

        RenderRectangle = new(zsortingTechnique);
        PushToInitializationQueue(renderRectangle);
    }

    public override void Render(float dt, object? obj = null)
    {
        // Background rendering

        // Bind the fb
        backgroundZBuffer.Bind();
        // Configure the viewport
        backgroundZBuffer.Viewport();
        // Clear the fb
        Engine.GL.Enable(EnableCap.Blend);
        Engine.GL.ClearColor(0, 0, 0, 0);
        Engine.GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

        // draw all children (should draw albedo = 2, and sprite_z = 1)
        base.Render(dt);

        FrameBufferObject.Unbind();

        renderRectangle.Technique = zsortingTechnique;

        renderRectangle.Render(dt);


        //// Bind the framebuffer and its attachments
        //FrameBuffer.Bind();

        //// set the viewport & clear screen
        //FrameBuffer.Viewport();
        //Engine.GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

        //// set to window frame buffer
        //if (Engine.Debugger.RenderToContainer) Engine.Debugger.GameContainerDebugger.FrameBuffer.Bind();
        //else FrameBufferObject.Unbind();

        //// restore window viewport
        //Engine.GL.Viewport(0, 0, (uint)(Engine.Debugger.RenderToContainer ? Engine.Debugger.GameContainerDebugger.FrameBuffer.Width : Engine.WindowManager.ViewportSize.X), (uint)(Engine.Debugger.RenderToContainer ? Engine.Debugger.GameContainerDebugger.FrameBuffer.Height : Engine.WindowManager.ViewportSize.Y));
        //Engine.GL.Clear(ClearBufferMask.ColorBufferBit);

        //// draw framebuffer to window
        //RenderRectangle.Render(dt);
    }
}