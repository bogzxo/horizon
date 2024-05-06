using Horizon.OpenGL.Buffers;

using ImGuiNET;

namespace Horizon.Engine.Debugging.Debuggers;

public class DockedGameContainerDebugger : DebuggerComponent
{
    public FrameBufferObject FrameBuffer { get; set; }

    public override void Initialize()
    {
        FrameBuffer = GameEngine.Instance.ObjectManager.FrameBuffers.CreateOrGet("container", new OpenGL.Descriptions.FrameBufferObjectDescription
        {
            Attachments = [
                Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0,
                Silk.NET.OpenGL.FramebufferAttachment.DepthAttachment
                ],
            Width = 800,
            Height = 600
        });

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
                    ].Handle,
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