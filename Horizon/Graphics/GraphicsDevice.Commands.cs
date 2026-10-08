using System.Numerics;

using Horizon.Graphics.Vulkan;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Horizon.Graphics;

/// <summary>
/// What goes into the command buffer, which is draws, dispatches, barriers, clears, copies, uploads and read backs, and the
/// rendering instances and image layouts those have to be wrapped in, which is the one thing Vulkan makes you do
/// that OpenGL did for you.
/// </summary>
public sealed unsafe partial class GraphicsDevice
{
    // What the bound target is to be cleared to the moment it is next drawn into, see Clear
    private Vector4? windowPendingClear;

    /* Drawing */

    /// <summary>Draws vertices out of the bound vertex array in order, without indices (or out of nothing, for a shader that makes them up).</summary>
    public void Draw(Topology topology, uint vertexCount, uint firstVertex = 0)
    {
        if (!PrepareDraw(topology)) return;

        Vk.CmdDraw(cmd, vertexCount, 1, firstVertex, 0);
        Statistics.Counting.DrawCalls++;
        Statistics.Counting.Instances++;
    }

    /// <summary>Draws vertices out of the bound vertex array, by their indices.</summary>
    public void DrawIndexed(Topology topology, uint indexCount, uint firstIndex = 0)
    {
        if (!PrepareDraw(topology)) return;

        Vk.CmdDrawIndexed(cmd, indexCount, 1, firstIndex, 0, 0);
        Statistics.Counting.DrawCalls++;
        Statistics.Counting.Instances++;
    }

    /// <summary>Draws the bound vertex array's indices so many times, each with its instance number, starting at a first instance.</summary>
    public void DrawIndexedInstanced(Topology topology, uint indexCount, uint instanceCount, uint firstInstance = 0)
    {
        if (instanceCount == 0 || !PrepareDraw(topology)) return;

        Vk.CmdDrawIndexed(cmd, indexCount, instanceCount, 0, 0, firstInstance);
        Statistics.Counting.DrawCalls++;
        Statistics.Counting.Instances += (int)instanceCount;
    }

    /// <summary>Draws so many vertices (made up by the shader, or out of the bound array) so many times, starting at a first instance.</summary>
    public void DrawInstanced(Topology topology, uint vertexCount, uint instanceCount, uint firstInstance = 0)
    {
        if (instanceCount == 0 || !PrepareDraw(topology)) return;

        Vk.CmdDraw(cmd, vertexCount, instanceCount, 0, firstInstance);
        Statistics.Counting.DrawCalls++;
        Statistics.Counting.Instances += (int)instanceCount;
    }

    /// <summary>
    /// Draws whatever the commands in a buffer say (VkDrawIndirectCommand, 16 bytes each), as many of them as another
    /// buffer says there are. For draws a compute pass worked out, see tilemap_cull.slang.
    /// </summary>
    public void DrawIndirectCount(Topology topology, GpuBuffer commands, nint commandsOffset, GpuBuffer count, nint countOffset, uint maxDraws)
    {
        if (maxDraws == 0 || !commands.IsValid || !count.IsValid || commands.Buffer.Handle == 0 || count.Buffer.Handle == 0) return;
        if (!PrepareDraw(topology)) return;

        Vk.CmdDrawIndirectCount(cmd, commands.Buffer, (ulong)commandsOffset, count.Buffer, (ulong)countOffset, maxDraws, 16);
        commands.LastUse = count.LastUse = NextSubmission;
        Statistics.Counting.DrawCalls++;
    }

    /// <summary>Draws what one VkDrawIndirectCommand in a buffer says, for a draw whose instance count a shader filled in.</summary>
    public void DrawIndirect(Topology topology, GpuBuffer command, nint offset = 0)
    {
        if (!command.IsValid || command.Buffer.Handle == 0) return;
        if (!PrepareDraw(topology)) return;

        Vk.CmdDrawIndirect(cmd, command.Buffer, (ulong)offset, 1, 16);
        command.LastUse = NextSubmission;
        Statistics.Counting.DrawCalls++;
    }

    /// <summary>Fills a stretch of a buffer with a value, in order with the draws. For zeroing the counters a compute pass adds to.</summary>
    public void FillBuffer(GpuBuffer buffer, uint value, nint offset = 0, nuint bytes = 0)
    {
        if (!buffer.IsValid || buffer.Buffer.Handle == 0) return;

        EndRendering();
        GlobalBarrier(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
        Vk.CmdFillBuffer(cmd, buffer.Buffer, (ulong)offset, bytes == 0 ? Vk.WholeSize : bytes, value);
        GlobalBarrier(PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
        buffer.LastUse = NextSubmission;
    }

    /// <summary>Fills a texture with a colour, whatever is bound. For the pictures that are blended into themselves and have to start empty.</summary>
    public void ClearTexture(Texture texture, Vector4 color)
    {
        if (!texture.IsValid) return;
        ClearImageNow(texture, color);
    }

    /* Compute */

    /// <summary>Runs the bound compute shader over so many work groups.</summary>
    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        if (!PrepareDispatch()) return;

        Vk.CmdDispatch(cmd, groupsX, groupsY, groupsZ);
        Statistics.Counting.Dispatches++;
    }

    /// <summary>Has what shaders wrote be there for whoever reads it next.</summary>
    public void Barrier(BarrierTargets targets)
    {
        EndRendering();

        PipelineStageFlags2 dstStage = PipelineStageFlags2.AllCommandsBit;
        AccessFlags2 dstAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
        if ((targets & BarrierTargets.VertexAttributes) != 0) dstAccess |= AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit | AccessFlags2.IndirectCommandReadBit;

        GlobalBarrier(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, dstStage, dstAccess);

        // What a compute shader wrote to an image is read as a texture next, which wants another layout
        foreach (Texture? image in boundImages)
        {
            if (image is { IsValid: true })
            {
                image.LastStage = PipelineStageFlags2.ComputeShaderBit;
                image.LastAccess = AccessFlags2.ShaderStorageWriteBit;
            }
        }
    }

    private void GlobalBarrier(PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };

        Vk.CmdPipelineBarrier2(cmd, in dependency);
        Statistics.Counting.Barriers++;
    }

    /* Layouts and rendering instances */

    /// <summary>
    /// Moves an image to a layout, with a barrier from whatever last touched it. Ends the rendering instance if one
    /// is on, barriers can't go inside one. For whoever records their own commands.
    /// </summary>
    public void Transition(Texture texture, ImageLayout layout, PipelineStageFlags2 stage, AccessFlags2 access)
    {
        if (!texture.IsValid || texture.Image.Handle == 0) return;

        bool sameLayout = texture.Layout == layout;
        bool readAfterRead = (texture.LastAccess & WriteAccess) == 0 && (access & WriteAccess) == 0;
        if (sameLayout && readAfterRead && texture.Layout != ImageLayout.Undefined)
        {
            texture.LastStage |= stage;
            texture.LastAccess |= access;
            return;
        }

        EndRendering();
        texture.Owner?.FlushPendingClears(this, texture);

        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = texture.LastStage,
            SrcAccessMask = texture.LastAccess,
            DstStageMask = stage,
            DstAccessMask = access,
            OldLayout = texture.Layout,
            NewLayout = layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = texture.Image,
            SubresourceRange = new ImageSubresourceRange(texture.Aspect, 0, texture.MipLevels, 0, 1)
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier
        };

        Vk.CmdPipelineBarrier2(cmd, in dependency);
        Statistics.Counting.Barriers++;

        texture.Layout = layout;
        texture.LastStage = stage;
        texture.LastAccess = access;
    }

    private void TransitionSwapchainImage(int index, ImageLayout layout, PipelineStageFlags2 stage, AccessFlags2 access)
    {
        ImageLayout old = swapchain.Layouts[index];
        if (old == layout) return;

        EndRendering();

        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = old == ImageLayout.Undefined ? PipelineStageFlags2.TopOfPipeBit : PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = old == ImageLayout.Undefined ? AccessFlags2.None : AccessFlags2.MemoryWriteBit | AccessFlags2.MemoryReadBit,
            DstStageMask = stage,
            DstAccessMask = access,
            OldLayout = old,
            NewLayout = layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = swapchain.Images[index],
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier
        };

        Vk.CmdPipelineBarrier2(cmd, in dependency);
        swapchain.Layouts[index] = layout;
    }

    /// <summary>Helper method to begin rendering into the bound target if it hasn't been begun, with whatever clears are pending.</summary>
    private void EnsureRendering()
    {
        if (renderingActive) return;

        var colors = stackalloc RenderingAttachmentInfo[ShaderPreprocessor.TEXTURE_BINDINGS];
        uint colorCount;
        RenderingAttachmentInfo depth = default, stencil = default;
        bool hasDepth = false, hasStencil = false;
        Extent2D extent;

        if (targetIsWindow)
        {
            int index = swapchain.Current;
            // The swapchain picture is a fresh one every frame, whatever it was cleared to is the load op
            TransitionSwapchainImage(index, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);

            colors[0] = Attachment(swapchain.Views[index], windowPendingClear);
            windowPendingClear = null;
            colorCount = 1;
            extent = swapchain.Extent;
        }
        else
        {
            RenderTarget target = boundTarget!;
            colorCount = (uint)target.ColorFormats.Length;
            for (int i = 0; i < colorCount; i++)
            {
                Texture? texture = target.ColorTextures[i];
                if (texture is null)
                {
                    colors[i] = new RenderingAttachmentInfo { SType = StructureType.RenderingAttachmentInfo, LoadOp = AttachmentLoadOp.DontCare, StoreOp = AttachmentStoreOp.DontCare };
                    continue;
                }

                Transition(texture, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);
                colors[i] = Attachment(texture.View, target.PendingColor[i]);
                target.PendingColor[i] = null;
            }

            if (target.DepthTexture is { } depthTexture)
            {
                Transition(depthTexture, ImageLayout.DepthStencilAttachmentOptimal, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit);

                depth = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = depthTexture.View,
                    ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    LoadOp = target.PendingDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(target.PendingDepthValue, (uint)target.PendingStencilValue) }
                };
                hasDepth = true;

                if (target.HasStencil)
                {
                    stencil = depth;
                    hasStencil = true;
                }

                target.PendingDepth = false;
            }

            extent = new Extent2D(target.Width, target.Height);
        }

        var info = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            LayerCount = 1,
            ColorAttachmentCount = colorCount,
            PColorAttachments = colors,
            PDepthAttachment = hasDepth ? &depth : null,
            PStencilAttachment = hasStencil ? &stencil : null
        };

        Vk.CmdBeginRendering(cmd, in info);
        renderingActive = true;
        renderingTarget = boundTarget;
        renderingWindow = targetIsWindow;
        viewportDirty = dynamicDirty = true;
    }

    private static RenderingAttachmentInfo Attachment(ImageView view, Vector4? clear) => new()
    {
        SType = StructureType.RenderingAttachmentInfo,
        ImageView = view,
        ImageLayout = ImageLayout.ColorAttachmentOptimal,
        LoadOp = clear.HasValue ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
        StoreOp = AttachmentStoreOp.Store,
        ClearValue = clear.HasValue ? new ClearValue { Color = new ClearColorValue(clear.Value.X, clear.Value.Y, clear.Value.Z, clear.Value.W) } : default
    };

    /// <summary>
    /// Ends the rendering instance, if one is on. Everything that isn't a draw does this itself; it is here for
    /// whoever records commands of their own. The next draw begins one again.
    /// </summary>
    public void EndRendering()
    {
        if (!renderingActive) return;

        Vk.CmdEndRendering(cmd);
        renderingActive = false;
        renderingTarget = null;
    }

    /* Clears */

    /// <summary>Wipes the bound target (the window, or a render target bound by somebody else).</summary>
    public void Clear(ClearTargets targets)
    {
        if (targetIsWindow)
        {
            if (!Frame.HasImage || (targets & ClearTargets.Color) == 0) return;

            if (renderingActive && renderingWindow) ClearAttachmentsNow([0], clearColor, false, false, 1.0f, 0);
            else windowPendingClear = clearColor;
            return;
        }

        RenderTarget target = boundTarget!;
        bool color = (targets & ClearTargets.Color) != 0;
        bool depth = (targets & ClearTargets.Depth) != 0 && target.DepthTexture is not null;
        bool stencil = (targets & ClearTargets.Stencil) != 0 && target.HasStencil;

        if (renderingActive && ReferenceEquals(renderingTarget, target))
        {
            var attachments = new List<int>();
            if (color) for (int i = 0; i < target.ColorTextures.Length; i++) if (target.ColorTextures[i] is not null) attachments.Add(i);
            ClearAttachmentsNow(attachments, clearColor, depth, stencil, 1.0f, 0);
            return;
        }

        if (color) for (int i = 0; i < target.ColorTextures.Length; i++) if (target.ColorTextures[i] is not null) target.PendingColor[i] = clearColor;
        if (depth || stencil)
        {
            target.PendingDepth = true;
            target.PendingDepthValue = 1.0f;
            target.PendingStencilValue = 0;
        }
    }

    /// <summary>Fills one colour attachment of a render target, whatever is bound.</summary>
    public void ClearColorAttachment(RenderTarget target, int attachment, Vector4 color)
    {
        if (!target.IsValid || attachment < 0 || attachment >= target.ColorTextures.Length || target.ColorTextures[attachment] is not { } texture) return;

        if (!targetIsWindow && ReferenceEquals(boundTarget, target))
        {
            if (renderingActive && ReferenceEquals(renderingTarget, target)) ClearAttachmentsNow([attachment], color, false, false, 1.0f, 0);
            else target.PendingColor[attachment] = color;
            return;
        }

        ClearImageNow(texture, color);
    }

    /// <summary>Wipes the depth and the stencil of a render target, whatever is bound.</summary>
    public void ClearDepthStencil(RenderTarget target, float depth = 1.0f, int stencil = 0)
    {
        if (!target.IsValid || target.DepthTexture is not { } texture) return;

        if (!targetIsWindow && ReferenceEquals(boundTarget, target))
        {
            if (renderingActive && ReferenceEquals(renderingTarget, target)) ClearAttachmentsNow([], Vector4.Zero, true, target.HasStencil, depth, stencil);
            else
            {
                target.PendingDepth = true;
                target.PendingDepthValue = depth;
                target.PendingStencilValue = stencil;
            }
            return;
        }

        ClearDepthImageNow(texture, depth, stencil);
    }

    private void ClearAttachmentsNow(IReadOnlyList<int> colorAttachments, Vector4 color, bool depth, bool stencil, float depthValue, int stencilValue)
    {
        var clears = stackalloc ClearAttachment[ShaderPreprocessor.TEXTURE_BINDINGS + 1];
        uint count = 0;

        foreach (int attachment in colorAttachments)
        {
            clears[count++] = new ClearAttachment
            {
                AspectMask = ImageAspectFlags.ColorBit,
                ColorAttachment = (uint)attachment,
                ClearValue = new ClearValue { Color = new ClearColorValue(color.X, color.Y, color.Z, color.W) }
            };
        }

        if (depth || stencil)
        {
            clears[count++] = new ClearAttachment
            {
                AspectMask = (depth ? ImageAspectFlags.DepthBit : 0) | (stencil ? ImageAspectFlags.StencilBit : 0),
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(depthValue, (uint)stencilValue) }
            };
        }

        if (count == 0) return;

        var rect = new ClearRect
        {
            Rect = new Rect2D(new Offset2D(0, 0), new Extent2D(viewportWidth, viewportHeight)),
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        Vk.CmdClearAttachments(cmd, count, clears, 1, in rect);
    }

    internal void ClearImageNow(Texture texture, Vector4 color)
    {
        Transition(texture, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        var value = new ClearColorValue(color.X, color.Y, color.Z, color.W);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, texture.MipLevels, 0, 1);
        Vk.CmdClearColorImage(cmd, texture.Image, ImageLayout.TransferDstOptimal, in value, 1, in range);
    }

    internal void ClearDepthImageNow(Texture texture, float depth, int stencil)
    {
        Transition(texture, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        var value = new ClearDepthStencilValue(depth, (uint)stencil);
        var range = new ImageSubresourceRange(texture.Aspect, 0, 1, 0, 1);
        Vk.CmdClearDepthStencilImage(cmd, texture.Image, ImageLayout.TransferDstOptimal, in value, 1, in range);
    }

    /// <summary>Helper method to do the clears a target was asked for and never got round to, because nothing was drawn into it after.</summary>
    private void FlushPendingClears(RenderTarget target) => target.FlushPendingClears(this, null);

    /* Copies */

    /// <summary>
    /// Copies the colours of the window as they are right now into the first attachment of a render target, stretched
    /// to fit if the two aren't the same size. Whatever is bound stays bound.
    /// </summary>
    public void CopyWindow(RenderTarget into, uint windowWidth, uint windowHeight, uint width, uint height)
    {
        if (!Frame.HasImage || swapchain.Current < 0 || into.ColorTextures.Length == 0 || into.ColorTextures[0] is not { } destination) return;

        EndRendering();
        int index = swapchain.Current;

        // Whatever the window was cleared to but hadn't been drawn into yet is what there is to copy
        if (windowPendingClear is { } pending)
        {
            TransitionSwapchainImage(index, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
            var value = new ClearColorValue(pending.X, pending.Y, pending.Z, pending.W);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            Vk.CmdClearColorImage(cmd, swapchain.Images[index], ImageLayout.TransferDstOptimal, in value, 1, in range);
            windowPendingClear = null;
        }

        TransitionSwapchainImage(index, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
        Transition(destination, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1)
        };
        blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
        blit.SrcOffsets[1] = new Offset3D((int)Math.Min(windowWidth, swapchain.Width), (int)Math.Min(windowHeight, swapchain.Height), 1);
        blit.DstOffsets[0] = new Offset3D(0, 0, 0);
        blit.DstOffsets[1] = new Offset3D((int)Math.Min(width, destination.Width), (int)Math.Min(height, destination.Height), 1);

        Vk.CmdBlitImage(cmd, swapchain.Images[index], ImageLayout.TransferSrcOptimal, destination.Image, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);

        TransitionSwapchainImage(index, ImageLayout.ColorAttachmentOptimal, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);
    }

    /* Uploads */

    /// <summary>
    /// Writes texels into part of a texture. The rows follow each other with nothing in between (<paramref name="format"/>
    /// says how wide a texel is). It happens in order with the draws, what was drawn with the texture before reads the old texels.
    /// </summary>
    public void UploadTexels(Texture texture, int x, int y, uint width, uint height, TexelFormat format, void* texels)
    {
        if (!texture.IsValid || width == 0 || height == 0) return;

        int bytesPerTexel = format == TexelFormat.R8 ? 1 : 4;
        ulong bytes = (ulong)width * height * (ulong)bytesPerTexel;

        EndRendering();

        LinearArena staging = Frame.Staging;
        ulong offset = staging.Take(bytes, 16);
        System.Buffer.MemoryCopy(texels, staging.Mapped + offset, bytes, bytes);
        Statistics.Counting.BytesUploaded += (long)bytes;

        Transition(texture, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D(width, height, 1)
        };

        Vk.CmdCopyBufferToImage(cmd, staging.Buffer, texture.Image, ImageLayout.TransferDstOptimal, 1, in region);
    }

    /// <inheritdoc cref="UploadTexels(Texture, int, int, uint, uint, TexelFormat, void*)"/>
    public void UploadTexels(Texture texture, int x, int y, uint width, uint height, TexelFormat format, ReadOnlySpan<byte> texels)
    {
        int needed = (int)(width * height * (format == TexelFormat.R8 ? 1 : 4));
        if (texels.Length < needed)
            throw new ArgumentException($"{texels.Length} bytes of texels for a {width} by {height} upload that wants {needed}.", nameof(texels));

        fixed (byte* pointer = texels)
            UploadTexels(texture, x, y, width, height, format, pointer);
    }

    /// <summary>Fills every mip level of a texture from the top one. The texture has to have been made with mip levels (<see cref="TextureDefinition.Mipmaps"/>).</summary>
    public void GenerateMipmaps(Texture texture)
    {
        if (!texture.IsValid || texture.MipLevels < 2) return;

        EndRendering();
        Transition(texture, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        int width = (int)texture.Width, height = (int)texture.Height;
        for (uint level = 1; level < texture.MipLevels; level++)
        {
            // The level above is read, the one being made written
            MipBarrier(texture, level - 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags2.TransferWriteBit, AccessFlags2.TransferReadBit);

            int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1)
            };
            blit.SrcOffsets[1] = new Offset3D(width, height, 1);
            blit.DstOffsets[1] = new Offset3D(nextWidth, nextHeight, 1);

            Vk.CmdBlitImage(cmd, texture.Image, ImageLayout.TransferSrcOptimal, texture.Image, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
            width = nextWidth;
            height = nextHeight;
        }

        // The last level never got read, so it is still a destination; everything above is a source. All of it read by shaders from here on
        MipBarrier(texture, texture.MipLevels - 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags2.TransferWriteBit, AccessFlags2.TransferReadBit);
        texture.Layout = ImageLayout.TransferSrcOptimal;
        texture.LastStage = PipelineStageFlags2.TransferBit;
        texture.LastAccess = AccessFlags2.TransferReadBit;
    }

    private void MipBarrier(Texture texture, uint level, ImageLayout from, ImageLayout to, AccessFlags2 srcAccess, AccessFlags2 dstAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = srcAccess,
            DstStageMask = PipelineStageFlags2.TransferBit,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = texture.Image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, level, 1, 0, 1)
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier
        };

        Vk.CmdPipelineBarrier2(cmd, in dependency);
    }

    /// <summary>Helper method to write data over part of a buffer, in order with the draws (see <see cref="GpuBuffer.Update{T}(ReadOnlySpan{T}, nint)"/>).</summary>
    internal void UpdateBuffer(GpuBuffer buffer, void* data, nuint bytes, nint byteOffset)
    {
        if (bytes == 0 || !buffer.IsValid || buffer.Buffer.Handle == 0) return;
        if ((ulong)byteOffset + bytes > buffer.Size)
            throw new ArgumentOutOfRangeException(nameof(bytes), $"{bytes} bytes at {byteOffset} don't fit in a buffer of {buffer.Size}.");

        // Never handed to the GPU and the CPU can see it, so it goes straight in
        if (buffer.LastUse == 0 && buffer.Memory.Mapped != null)
        {
            System.Buffer.MemoryCopy(data, buffer.Memory.Mapped + byteOffset, bytes, bytes);
            return;
        }

        EndRendering();

        // Whoever read it before has to be done before it is written over
        GlobalBarrier(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);

        if (bytes <= 65536 && bytes % 4 == 0 && byteOffset % 4 == 0)
        {
            Vk.CmdUpdateBuffer(cmd, buffer.Buffer, (ulong)byteOffset, bytes, data);
        }
        else
        {
            LinearArena staging = Frame.Staging;
            ulong offset = staging.Take(bytes, 16);
            System.Buffer.MemoryCopy(data, staging.Mapped + offset, bytes, bytes);

            var region = new BufferCopy(offset, (ulong)byteOffset, bytes);
            Vk.CmdCopyBuffer(cmd, staging.Buffer, buffer.Buffer, 1, in region);
        }

        GlobalBarrier(PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
        Statistics.Counting.BytesUploaded += (long)bytes;
        buffer.LastUse = NextSubmission;
    }

    /// <summary>Helper method to fill a whole buffer that lives on the card, through the staging arena.</summary>
    internal void FillBuffer(GpuBuffer buffer, void* data, nuint bytes)
    {
        if (buffer.Memory.Mapped != null)
        {
            System.Buffer.MemoryCopy(data, buffer.Memory.Mapped, bytes, bytes);
            Statistics.Counting.BytesUploaded += (long)bytes;
            return;
        }

        EndRendering();
        LinearArena staging = Frame.Staging;
        ulong offset = staging.Take(bytes, 16);
        System.Buffer.MemoryCopy(data, staging.Mapped + offset, bytes, bytes);

        var region = new BufferCopy(offset, 0, bytes);
        Vk.CmdCopyBuffer(cmd, staging.Buffer, buffer.Buffer, 1, in region);
        GlobalBarrier(PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
        Statistics.Counting.BytesUploaded += (long)bytes;
        buffer.LastUse = NextSubmission;
    }

    /* Read backs */

    /// <summary>
    /// Reads the colours of the window as it is right now (or the bound render target's first attachment), four bytes a
    /// pixel red first, bottom row first. Slow, the GPU is caught up with first. For screenshots and tests.
    /// </summary>
    public void ReadPixels(int x, int y, uint width, uint height, Span<byte> rgba)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("Not enough room for the pixels.", nameof(rgba));
        if (width == 0 || height == 0) return;

        EndRendering();

        Image image;
        Format format;
        ImageLayout restore;
        Texture? texture = null;
        uint imageHeight;

        if (targetIsWindow)
        {
            if (!Frame.HasImage || swapchain.Current < 0) return;

            if (windowPendingClear is { } pending)
            {
                TransitionSwapchainImage(swapchain.Current, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit);
                var clear = new ClearColorValue(pending.X, pending.Y, pending.Z, pending.W);
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
                Vk.CmdClearColorImage(cmd, swapchain.Images[swapchain.Current], ImageLayout.TransferDstOptimal, in clear, 1, in range);
                windowPendingClear = null;
            }

            TransitionSwapchainImage(swapchain.Current, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
            image = swapchain.Images[swapchain.Current];
            format = swapchain.Format;
            restore = ImageLayout.ColorAttachmentOptimal;
            imageHeight = swapchain.Height;
        }
        else
        {
            if (boundTarget!.ColorTextures.Length == 0 || boundTarget.ColorTextures[0] is not { } first) return;
            texture = first;
            Transition(texture, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
            image = texture.Image;
            format = texture.VkFormat;
            restore = ImageLayout.TransferSrcOptimal;
            imageHeight = texture.Height;
        }

        ulong bytes = (ulong)width * height * 4;
        var (staging, memory) = MakeHostBuffer(bytes, BufferUsageFlags.TransferDstBit);

        // Rows are counted from the top here and from the bottom by whoever asked
        int top = (int)imageHeight - y - (int)height;
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x, Math.Max(0, top), 0),
            ImageExtent = new Extent3D(width, height, 1)
        };
        Vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, staging, 1, in region);

        if (targetIsWindow) TransitionSwapchainImage(swapchain.Current, restore, PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);

        ulong value = Flush();
        WaitTimeline(value, ulong.MaxValue);

        bool bgra = format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
        int rowBytes = (int)width * 4;
        for (int row = 0; row < height; row++)
        {
            var source = new ReadOnlySpan<byte>(memory.Mapped + (long)row * rowBytes, rowBytes);
            var destination = rgba.Slice((int)(height - 1 - row) * rowBytes, rowBytes);
            source.CopyTo(destination);

            if (bgra)
            {
                for (int i = 0; i < rowBytes; i += 4)
                    (destination[i], destination[i + 2]) = (destination[i + 2], destination[i]);
            }
        }

        Vk.DestroyBuffer(Device, staging, null);
        Memory.Free(memory);
    }

    /// <summary>Helper method to read a stretch of a buffer back, which waits for the GPU. For tools and tests.</summary>
    internal void ReadBuffer(GpuBuffer buffer, void* into, nuint bytes, nint byteOffset)
    {
        if (!buffer.IsValid || buffer.Buffer.Handle == 0 || bytes == 0) return;

        EndRendering();
        GlobalBarrier(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);

        if (buffer.Memory.Mapped != null)
        {
            ulong done = Flush();
            WaitTimeline(done, ulong.MaxValue);
            System.Buffer.MemoryCopy(buffer.Memory.Mapped + byteOffset, into, bytes, bytes);
            return;
        }

        var (staging, memory) = MakeHostBuffer(bytes, BufferUsageFlags.TransferDstBit);
        var region = new BufferCopy((ulong)byteOffset, 0, bytes);
        Vk.CmdCopyBuffer(cmd, buffer.Buffer, staging, 1, in region);

        ulong value = Flush();
        WaitTimeline(value, ulong.MaxValue);
        System.Buffer.MemoryCopy(memory.Mapped, into, bytes, bytes);

        Vk.DestroyBuffer(Device, staging, null);
        Memory.Free(memory);
    }

    private (Buffer, VulkanMemory.Allocation) MakeHostBuffer(ulong bytes, BufferUsageFlags usage)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bytes,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        VulkanContext.Check(Vk.CreateBuffer(Device, in info, null, out Buffer buffer), "making a read back buffer");
        Context.Name(ObjectType.Buffer, buffer.Handle, $"read back of {bytes} bytes");
        Vk.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements requirements);
        var memory = Memory.Allocate(requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.HostCachedBit, forImage: false);
        VulkanContext.Check(Vk.BindBufferMemory(Device, buffer, memory.Memory, memory.Offset), "binding a read back buffer");
        return (buffer, memory);
    }
}
