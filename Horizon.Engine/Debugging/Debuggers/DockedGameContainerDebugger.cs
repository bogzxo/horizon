using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using ImGuiNET;
using Silk.NET.OpenGL;

using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

namespace Horizon.Engine.Debugging.Debuggers;

public class DockedGameContainerDebugger : DebuggerComponent
{
    public FrameBufferObject FrameBuffer { get; set; }

    public override void Initialize()
    {
        if (GameEngine.Instance.ObjectManager.FrameBuffers.TryCreateOrGet("container", new OpenGL.Descriptions.FrameBufferObjectDescription
        {
            Attachments = new() {
                { FramebufferAttachment.ColorAttachment0, FrameBufferAttachmentDefinition.TextureRGBAByte },
                { FramebufferAttachment.DepthAttachment, FrameBufferAttachmentDefinition.TextureDepth },
            },
            Width = 800,
            Height = 600
        }, out var result))
        {
            FrameBuffer = result.Asset;
        }
        else
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

        Name = "Game Container";
    }

    public override void UpdateState(float dt)
    { }

    public override void UpdatePhysics(float dt)
    { }

    public override void Render(float dt, object? obj = null)
    {
        if (Visible && ImGui.Begin("Game Container"))
        {
            ImGui.Image(
                (nint)
                    FrameBuffer.Attachments[
                        Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0
                    ].Texture.Handle,
                new System.Numerics.Vector2(FrameBuffer.Width, FrameBuffer.Height),
                new System.Numerics.Vector2(0, 1),
                new System.Numerics.Vector2(1, 0)
            );

            ImGui.End();
        }
    }

    public override void Dispose()
    { }
}