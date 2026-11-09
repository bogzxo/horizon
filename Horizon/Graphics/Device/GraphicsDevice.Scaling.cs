using System.Numerics;

using Silk.NET.Vulkan;

namespace Horizon.Graphics;

/// <summary>
/// The window standing in for a target of another size while the frame's scene is drawn small, see
/// <see cref="Horizon.Rendering.Scaling.FrameScaler"/>. Binding the window binds the scene's target then, clears,
/// copies and read backs of the window are of it, and everything that only ever asks for the window draws at the
/// scene's size without being told.
/// </summary>
public sealed unsafe partial class GraphicsDevice
{
    // What the window is while the scene is drawn small, null the rest of the time
    private RenderTarget? sceneTarget;

    /// <summary>
    /// Has a target be what binding the window binds from here on, null to have the window be itself again. Whoever
    /// had the window bound has the new one bound in its place. Render thread, between draws.
    /// </summary>
    internal void StandInForWindow(RenderTarget? target)
    {
        if (ReferenceEquals(sceneTarget, target))
            return;

        bool windowBound = boundIsWindow;
        sceneTarget = target is { IsValid: true } ? target : null;
        if (windowBound)
            BindRenderTarget(null);
    }

    /// <summary>The target standing in for the window right now, null while the window is itself.</summary>
    internal RenderTarget? WindowStandIn => sceneTarget;

    /// <summary>
    /// How big what binding the window draws into is right now, in pixels. The swapchain's size, or the scene's while
    /// it's drawn small.
    /// </summary>
    public Vector2 WindowTargetSize => sceneTarget is { } scene
        ? new Vector2(scene.Width, scene.Height)
        : new Vector2(swapchain.Width, swapchain.Height);

    /// <summary>Helper method for <see cref="CopyWindow"/> while the scene stands in for the window, a blit of its colours.</summary>
    private void CopySceneTarget(RenderTarget scene, RenderTarget into, uint width, uint height)
    {
        if (scene.ColorTextures.Length == 0 || scene.ColorTextures[0] is not { } source || into.ColorTextures[0] is not { } destination)
            return;

        EndRendering();
        scene.FlushPendingClears(this, null);

        Transition(source, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
        Transition(destination, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1)
        };
        blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
        blit.SrcOffsets[1] = new Offset3D((int)source.Width, (int)source.Height, 1);
        blit.DstOffsets[0] = new Offset3D(0, 0, 0);
        blit.DstOffsets[1] = new Offset3D((int)Math.Min(width, destination.Width), (int)Math.Min(height, destination.Height), 1);

        Vk.CmdBlitImage(cmd, source.Image, ImageLayout.TransferSrcOptimal, destination.Image, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
    }
}
