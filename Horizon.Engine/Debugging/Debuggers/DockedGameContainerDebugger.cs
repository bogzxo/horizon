#if DEBUG
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Egui;
using Egui.Containers;
using Egui.Widgets;
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
            Width = 854,
            Height = 480
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

    public override void RenderUi(Ui root)
    {
        if (!Visible) return;

        new Window("Game Container")
            .Show(root.Ctx, ui =>
            {
                var handle = FrameBuffer.Attachments[Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0].Texture.Handle;
                // Note: Egui doesn't support immediate arbitrary texture rendering without registering it in the texture manager
                // Assumes Egui.TextureId exists or handle can be passed. Fallback to basic label if texture binding is missing.
                ui.Label($"[Game Container: Texture {handle}, {FrameBuffer.Width}x{FrameBuffer.Height}]");
            });
    }


    public override void Dispose()
    { }
}
#endif