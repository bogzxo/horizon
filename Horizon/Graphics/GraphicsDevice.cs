using System.Numerics;

using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;

namespace Horizon.Graphics;

/// <summary>What a run of vertices is drawn as.</summary>
public enum Topology
{
    Triangles,
    TriangleStrip,
    Lines,
    LineStrip,
    Points
}

/// <summary>What a clear of the window (or a frame buffer) wipes.</summary>
[Flags]
public enum ClearTargets
{
    Color = 1,
    Depth = 2,
    Stencil = 4,
    All = Color | Depth | Stencil
}

/// <summary>How a stencil (or depth) test compares.</summary>
public enum CompareFunction
{
    Never,
    Always,
    Less,
    LessEqual,
    Equal,
    NotEqual,
    Greater,
    GreaterEqual
}

/// <summary>What happens to a stencil value when a test goes one way or the other.</summary>
public enum StencilAction
{
    Keep,
    Zero,
    Replace,
    Increment,
    Decrement,
    Invert
}

/// <summary>How the texels handed to <see cref="GraphicsDevice.UploadTexels"/> are laid out in memory.</summary>
public enum TexelFormat
{
    /// <summary>Four bytes a texel, red first.</summary>
    Rgba8,

    /// <summary>One byte a texel.</summary>
    R8
}

/// <summary>What a memory barrier has to wait for before the next thing reads it.</summary>
[Flags]
public enum BarrierTargets
{
    /// <summary>Storage buffers written by a shader.</summary>
    ShaderStorage = 1,

    /// <summary>Buffers written through uploads.</summary>
    BufferUpdate = 2,

    /// <summary>Vertex and index buffers about to be drawn from.</summary>
    VertexAttributes = 4,

    /// <summary>Images written by a shader.</summary>
    ShaderImages = 8
}

/// <summary>How much the backend's own debugging is worried about something it was asked to do.</summary>
public enum DebugLevel
{
    Note,
    Low,
    Medium,
    High
}

/// <summary>How a sampler reads a texture, see <see cref="GraphicsDevice.CreateSampler"/>.</summary>
/// <param name="Smooth">Blended between the texels, nearest texel otherwise.</param>
/// <param name="Mipmaps">Read from the mip levels when drawn smaller, for a texture that has them.</param>
/// <param name="Repeat">Wrap around at the edges, clamp to them otherwise.</param>
public readonly record struct SamplerSettings(bool Smooth, bool Mipmaps = false, bool Repeat = false);

/// <summary>
/// The GPU, as far as the renderers are concerned. Everything that draws goes through here (and through the resource
/// classes, buffers, textures, vertex arrays, techniques), never through the API underneath, so another backend can be
/// put behind it later. There is one, <see cref="Current"/>, made by the window along with its context.
/// <code>
/// var device = GraphicsDevice.Current;
/// mesh.Bind();
/// device.DrawIndexedInstanced(Topology.Triangles, 6, count);
/// </code>
/// Render thread only, like everything that touches the GPU. What is not in here yet goes in here, not around it,
/// see CLAUDE.md.
/// </summary>
public abstract class GraphicsDevice
{
    private static GraphicsDevice? current;

    /// <summary>The device there is. Throws before the window has made one.</summary>
    public static GraphicsDevice Current => current ?? throw new InvalidOperationException("There is no graphics device yet, the window makes it along with its context.");

    /// <summary>Whether there is a device to be had yet.</summary>
    public static bool IsAvailable => current is not null;

    protected static void Install(GraphicsDevice device) => current = device;

    /// <summary>What the backend calls itself, with the version it got, for the log and the overlays.</summary>
    public abstract string Description { get; }

    /// <summary>The widest (and tallest) a texture can be, in texels.</summary>
    public abstract uint MaxTextureSize { get; }

    /* Drawing */

    /// <summary>Draws vertices out of the bound vertex array, by their indices.</summary>
    public abstract void DrawIndexed(Topology topology, uint indexCount, uint firstIndex = 0);

    /// <summary>Draws the bound vertex array's indices so many times, each with its instance number, starting at a first instance.</summary>
    public abstract void DrawIndexedInstanced(Topology topology, uint indexCount, uint instanceCount, uint firstInstance = 0);

    /// <summary>Draws vertices out of the bound vertex array in order, without indices.</summary>
    public abstract void Draw(Topology topology, uint vertexCount, uint firstVertex = 0);

    /* Compute */

    /// <summary>Runs the bound compute technique over so many work groups.</summary>
    public abstract void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1);

    /// <summary>Has what shaders wrote be there for whoever reads it next.</summary>
    public abstract void Barrier(BarrierTargets targets);

    /* What is drawn into */

    /// <summary>Makes the window what is drawn into.</summary>
    public abstract void BindWindow();

    /// <summary>Sets what of the bound target is drawn to, in pixels from its bottom left.</summary>
    public abstract void SetViewport(int x, int y, uint width, uint height);

    /// <summary>What <see cref="Clear"/> fills the window with.</summary>
    public abstract Vector4 ClearColor { get; set; }

    /// <summary>Wipes the bound target (the window, or a frame buffer bound by somebody else).</summary>
    public abstract void Clear(ClearTargets targets);

    /// <summary>Fills one colour attachment of a frame buffer, whatever is bound.</summary>
    public abstract void ClearColorAttachment(FrameBufferObject frameBuffer, int attachment, Vector4 color);

    /// <summary>Wipes the depth and the stencil of a frame buffer, whatever is bound. Writes to both are switched on first.</summary>
    public abstract void ClearDepthStencil(FrameBufferObject frameBuffer, float depth = 1.0f, int stencil = 0);

    /// <summary>
    /// Copies the colours of the window as they are right now into the first attachment of a frame buffer, stretched to
    /// fit if the two aren't the same size. Whatever is bound stays bound.
    /// </summary>
    public abstract void CopyWindow(FrameBufferObject into, uint windowWidth, uint windowHeight, uint width, uint height);

    /* State the renderers set around their draws (blending and the depth test are RenderState's) */

    public abstract void SetColorWrite(bool enabled);
    public abstract void SetDepthWrite(bool enabled);
    public abstract void SetStencilTest(bool enabled);
    public abstract void SetStencilWrite(uint mask);
    public abstract void SetStencilFunction(CompareFunction function, int reference, uint mask);
    public abstract void SetStencilOperation(StencilAction onFail, StencilAction onDepthFail, StencilAction onPass);

    /* Textures and samplers */

    /// <summary>Binds textures to a run of units in one go, the first to <paramref name="firstUnit"/>. A handle of 0 leaves nothing on its unit.</summary>
    public abstract void BindTextures(ReadOnlySpan<uint> handles, uint firstUnit = 0);

    /// <summary>Binds samplers to a run of units in one go. A handle of 0 has the unit go by the texture's own settings.</summary>
    public abstract void BindSamplers(ReadOnlySpan<uint> samplers, uint firstUnit = 0);

    /// <summary>Makes a sampler, for drawing a texture with other settings than its own.</summary>
    public abstract uint CreateSampler(SamplerSettings settings);

    public abstract void DeleteSampler(uint sampler);

    /// <summary>Gives a texture every mip level it can have and fills them in from the top one.</summary>
    public abstract void GenerateMipmaps(Texture texture);

    /// <summary>
    /// Writes texels into part of a texture. The rows follow each other with nothing in between (<paramref name="format"/> says how wide a texel is).
    /// </summary>
    public abstract unsafe void UploadTexels(Texture texture, int x, int y, uint width, uint height, TexelFormat format, void* texels);

    /// <inheritdoc cref="UploadTexels(Texture, int, int, uint, uint, TexelFormat, void*)"/>
    public unsafe void UploadTexels(Texture texture, int x, int y, uint width, uint height, TexelFormat format, ReadOnlySpan<byte> texels)
    {
        int needed = (int)(width * height * (format == TexelFormat.R8 ? 1 : 4));
        if (texels.Length < needed)
            throw new ArgumentException($"{texels.Length} bytes of texels for a {width} by {height} upload that wants {needed}.", nameof(texels));

        fixed (byte* pointer = texels)
            UploadTexels(texture, x, y, width, height, format, pointer);
    }

    /// <summary>
    /// Reads the colours of the window as it is right now (or the bound frame buffer's first attachment), four bytes a pixel, bottom row first.
    /// Slow, the GPU is caught up with first. For screenshots and tests.
    /// </summary>
    public abstract void ReadPixels(int x, int y, uint width, uint height, Span<byte> rgba);

    /* The backend's own debugging */

    /// <summary>
    /// Has the backend say what it makes of what it is asked to do, through a callback (the message, how worried it is,
    /// and its number for telling the known ones apart). Nothing happens on a backend that has no such thing.
    /// </summary>
    public abstract void OnDebugMessage(Action<string, DebugLevel, int> handler);

    /* Fences */

    /// <summary>Drops a fence behind everything submitted so far, see <see cref="WaitFence"/>.</summary>
    public abstract nint CreateFence();

    /// <summary>Waits until the GPU has got past a fence, or a time (in nanoseconds) has gone by. True if it got past it.</summary>
    public abstract bool WaitFence(nint fence, ulong timeoutNanoseconds);

    public abstract void DeleteFence(nint fence);
}
