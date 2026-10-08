using System.Numerics;

using Horizon.Graphics;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.OpenGL;

/// <summary>
/// The OpenGL (4.5 with direct state access, 4.6 for the shaders) side of <see cref="GraphicsDevice"/>. Installed by
/// <see cref="Managers.ObjectManager.SetGL"/> once the window has a context. Nothing in here is clever, it is the one
/// place the renderers' intentions turn into GL calls.
/// </summary>
public sealed class OpenGLDevice : GraphicsDevice
{
    private readonly GL gl;
    private Vector4 clearColor;

    // The driver calls back into this, so it has to be kept from the collector for as long as the device lives
    private DebugProc? debugProc;
    private Action<string, DebugLevel, int>? debugHandler;

    internal OpenGLDevice(GL gl)
    {
        this.gl = gl;
        Description = $"OpenGL {gl.GetStringS(StringName.Version)} on {gl.GetStringS(StringName.Renderer)}";
        MaxTextureSize = (uint)Math.Max(gl.GetInteger(GetPName.MaxTextureSize), 1024);
        Install(this);
    }

    public override string Description { get; }

    public override uint MaxTextureSize { get; }

    private static PrimitiveType Of(Topology topology) => topology switch
    {
        Topology.Triangles => PrimitiveType.Triangles,
        Topology.TriangleStrip => PrimitiveType.TriangleStrip,
        Topology.Lines => PrimitiveType.Lines,
        Topology.LineStrip => PrimitiveType.LineStrip,
        _ => PrimitiveType.Points
    };

    private static StencilFunction Of(CompareFunction function) => function switch
    {
        CompareFunction.Never => StencilFunction.Never,
        CompareFunction.Less => StencilFunction.Less,
        CompareFunction.LessEqual => StencilFunction.Lequal,
        CompareFunction.Equal => StencilFunction.Equal,
        CompareFunction.NotEqual => StencilFunction.Notequal,
        CompareFunction.Greater => StencilFunction.Greater,
        CompareFunction.GreaterEqual => StencilFunction.Gequal,
        _ => StencilFunction.Always
    };

    private static StencilOp Of(StencilAction action) => action switch
    {
        StencilAction.Zero => StencilOp.Zero,
        StencilAction.Replace => StencilOp.Replace,
        StencilAction.Increment => StencilOp.Incr,
        StencilAction.Decrement => StencilOp.Decr,
        StencilAction.Invert => StencilOp.Invert,
        _ => StencilOp.Keep
    };

    public override unsafe void DrawIndexed(Topology topology, uint indexCount, uint firstIndex = 0) =>
        gl.DrawElements(Of(topology), indexCount, DrawElementsType.UnsignedInt, (void*)(firstIndex * sizeof(uint)));

    public override unsafe void DrawIndexedInstanced(Topology topology, uint indexCount, uint instanceCount, uint firstInstance = 0)
    {
        if (firstInstance == 0)
            gl.DrawElementsInstanced(Of(topology), indexCount, DrawElementsType.UnsignedInt, null, instanceCount);
        else
            gl.DrawElementsInstancedBaseInstance(Of(topology), indexCount, DrawElementsType.UnsignedInt, null, instanceCount, firstInstance);
    }

    public override void Draw(Topology topology, uint vertexCount, uint firstVertex = 0) =>
        gl.DrawArrays(Of(topology), (int)firstVertex, vertexCount);

    public override void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1) => gl.DispatchCompute(groupsX, groupsY, groupsZ);

    public override void Barrier(BarrierTargets targets)
    {
        MemoryBarrierMask mask = 0;
        if ((targets & BarrierTargets.ShaderStorage) != 0) mask |= MemoryBarrierMask.ShaderStorageBarrierBit;
        if ((targets & BarrierTargets.BufferUpdate) != 0) mask |= MemoryBarrierMask.BufferUpdateBarrierBit;
        if ((targets & BarrierTargets.VertexAttributes) != 0) mask |= MemoryBarrierMask.VertexAttribArrayBarrierBit;
        if ((targets & BarrierTargets.ShaderImages) != 0) mask |= MemoryBarrierMask.ShaderImageAccessBarrierBit;
        gl.MemoryBarrier(mask);
    }

    public override void BindWindow() => gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

    public override void SetViewport(int x, int y, uint width, uint height) => gl.Viewport(x, y, width, height);

    public override Vector4 ClearColor
    {
        get => clearColor;
        set
        {
            clearColor = value;
            gl.ClearColor(value.X, value.Y, value.Z, value.W);
        }
    }

    public override void Clear(ClearTargets targets)
    {
        ClearBufferMask mask = 0;
        if ((targets & ClearTargets.Color) != 0) mask |= ClearBufferMask.ColorBufferBit;
        if ((targets & ClearTargets.Depth) != 0) mask |= ClearBufferMask.DepthBufferBit;
        if ((targets & ClearTargets.Stencil) != 0) mask |= ClearBufferMask.StencilBufferBit;
        gl.Clear(mask);
    }

    public override unsafe void ClearColorAttachment(FrameBufferObject frameBuffer, int attachment, Vector4 color) =>
        gl.ClearNamedFramebuffer(frameBuffer.Handle, BufferKind.Color, attachment, (float*)&color);

    public override void ClearDepthStencil(FrameBufferObject frameBuffer, float depth = 1.0f, int stencil = 0)
    {
        // Only what can be written to gets cleared, and whoever drew last might have left these off
        gl.DepthMask(true);
        gl.StencilMask(0xFF);
        gl.ClearNamedFramebuffer(frameBuffer.Handle, GLEnum.DepthStencil, 0, depth, stencil);
    }

    public override void SetColorWrite(bool enabled) => gl.ColorMask(enabled, enabled, enabled, enabled);

    public override void SetDepthWrite(bool enabled) => gl.DepthMask(enabled);

    public override void SetStencilTest(bool enabled)
    {
        if (enabled) gl.Enable(EnableCap.StencilTest);
        else gl.Disable(EnableCap.StencilTest);
    }

    public override void SetStencilWrite(uint mask) => gl.StencilMask(mask);

    public override void SetStencilFunction(CompareFunction function, int reference, uint mask) => gl.StencilFunc(Of(function), reference, mask);

    public override void SetStencilOperation(StencilAction onFail, StencilAction onDepthFail, StencilAction onPass) =>
        gl.StencilOp(Of(onFail), Of(onDepthFail), Of(onPass));

    public override unsafe void BindTextures(ReadOnlySpan<uint> handles, uint firstUnit = 0)
    {
        fixed (uint* pointer = handles)
            gl.BindTextures(firstUnit, (uint)handles.Length, pointer);
    }

    public override unsafe void BindSamplers(ReadOnlySpan<uint> samplers, uint firstUnit = 0)
    {
        fixed (uint* pointer = samplers)
            gl.BindSamplers(firstUnit, (uint)samplers.Length, pointer);
    }

    public override uint CreateSampler(SamplerSettings settings)
    {
        uint sampler = gl.CreateSampler();

        GLEnum minify = settings.Smooth
            ? settings.Mipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear
            : settings.Mipmaps ? GLEnum.NearestMipmapNearest : GLEnum.Nearest;

        gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)minify);
        gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)(settings.Smooth ? GLEnum.Linear : GLEnum.Nearest));
        gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)(settings.Repeat ? GLEnum.Repeat : GLEnum.ClampToEdge));
        gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)(settings.Repeat ? GLEnum.Repeat : GLEnum.ClampToEdge));
        return sampler;
    }

    public override void DeleteSampler(uint sampler) => gl.DeleteSampler(sampler);

    public override void GenerateMipmaps(Texture texture)
    {
        gl.TextureParameter(texture.Handle, TextureParameterName.TextureMaxLevel, 1000);
        gl.GenerateTextureMipmap(texture.Handle);
    }

    public override unsafe void UploadTexels(Texture texture, int x, int y, uint width, uint height, TexelFormat format, void* texels)
    {
        // The rows come packed, the default is to expect them padded to four bytes
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        gl.TextureSubImage2D(
            texture.Handle, 0, x, y, width, height,
            format == TexelFormat.R8 ? PixelFormat.Red : PixelFormat.Rgba,
            PixelType.UnsignedByte,
            texels);

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    }

    public override unsafe void ReadPixels(int x, int y, uint width, uint height, Span<byte> rgba)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("Not enough room for the pixels.", nameof(rgba));

        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* pointer = rgba)
            gl.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pointer);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
    }

    public override void CopyWindow(FrameBufferObject into, uint windowWidth, uint windowHeight, uint width, uint height) =>
        // Zero is the window, the one frame buffer nobody had to make
        gl.BlitNamedFramebuffer(
            0, into.Handle,
            0, 0, (int)windowWidth, (int)windowHeight,
            0, 0, (int)width, (int)height,
            ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Linear);

    public override unsafe void OnDebugMessage(Action<string, DebugLevel, int> handler)
    {
        debugHandler = handler;
        if (debugProc is not null) return;

        gl.Enable(EnableCap.DebugOutput);
        gl.DebugMessageCallback(debugProc = Relay, null);
    }

    private void Relay(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        var level = severity switch
        {
            GLEnum.DebugSeverityHigh => DebugLevel.High,
            GLEnum.DebugSeverityMedium => DebugLevel.Medium,
            GLEnum.DebugSeverityLow => DebugLevel.Low,
            _ => DebugLevel.Note
        };

        debugHandler?.Invoke($"[{source}] [{type}] {System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message)}", level, id);
    }

    public override nint CreateFence() => gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);

    public override bool WaitFence(nint fence, ulong timeoutNanoseconds)
    {
        var status = (GLEnum)gl.ClientWaitSync(fence, SyncObjectMask.Bit, timeoutNanoseconds);
        return status is GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied;
    }

    public override void DeleteFence(nint fence) => gl.DeleteSync(fence);
}
