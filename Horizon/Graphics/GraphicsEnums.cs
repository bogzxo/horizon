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

/// <summary>What a clear of the window (or a render target) wipes.</summary>
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

/// <summary>How much the device's own debugging (the validation layer, the driver) is worried about something it was asked to do.</summary>
public enum DebugLevel
{
    Note,
    Low,
    Medium,
    High
}

/// <summary>What the texels of a texture are made of.</summary>
public enum PixelFormat
{
    /// <summary>Four bytes, red first, read as 0 to 1.</summary>
    Rgba8,

    /// <summary>The same, but taken to be sRGB and handed to the shader in linear light.</summary>
    Rgba8Srgb,

    /// <summary>One byte, read as 0 to 1.</summary>
    R8,

    /// <summary>One half, for a field of distances.</summary>
    R16F,

    /// <summary>Two halves.</summary>
    Rg16F,
    Rg32F,

    /// <summary>Four halves, for pictures that are blended into themselves frame after frame.</summary>
    Rgba16F,

    /// <summary>Four floats, for data rather than colour.</summary>
    Rgba32F,

    /// <summary>24 bits of depth and 8 of stencil.</summary>
    Depth24Stencil8,

    /// <summary>32 bits of depth, no stencil.</summary>
    Depth32F
}

/// <summary>What a buffer is for, which decides what the GPU lets it be bound as.</summary>
[Flags]
public enum BufferUsage
{
    Vertex = 1,
    Index = 2,
    Storage = 4,
    Uniform = 8,
    Indirect = 16
}

/// <summary>How a buffer is written to.</summary>
public enum BufferAccess
{
    /// <summary>Filled now and then with <see cref="GpuBuffer.Upload{T}(ReadOnlySpan{T})"/> and read by the GPU after. New memory every time, so a buffer still being read is never written over.</summary>
    Static,

    /// <summary>Written over in parts, in order with the draws (<see cref="GpuBuffer.Update{T}(ReadOnlySpan{T}, nint)"/>), and read and written by shaders. Lives on the card.</summary>
    Dynamic,

    /// <summary>Mapped for good and written straight into by the CPU every frame, see <see cref="StreamBuffer{T}"/>.</summary>
    Stream
}

/// <summary>A stage of a shader program.</summary>
public enum ShaderStage
{
    Vertex,
    Fragment,
    Compute,
    Geometry
}

/// <summary>How much of a colour (or of what is there already) goes into a blend, see <see cref="BlendMode"/>.</summary>
public enum BlendFactor
{
    Zero,
    One,
    SrcColor,
    OneMinusSrcColor,
    DstColor,
    OneMinusDstColor,
    SrcAlpha,
    OneMinusSrcAlpha,
    DstAlpha,
    OneMinusDstAlpha
}

/// <summary>Where an attachment of a render target sits, which is also which output of a fragment shader it takes.</summary>
public enum AttachmentPoint
{
    Color0 = 0,
    Color1,
    Color2,
    Color3,
    Color4,
    Color5,
    Color6,
    Color7,
    DepthStencil = 100,
    Depth = 101
}

/// <summary>What a texture is used for besides being read by a shader, said up front so the image is made to fit.</summary>
[Flags]
public enum TextureUsage
{
    /// <summary>Read through a sampler.</summary>
    Sampled = 1,

    /// <summary>Drawn into as an attachment of a render target.</summary>
    RenderTarget = 2,

    /// <summary>Written (and read) by compute shaders as an image.</summary>
    Storage = 4,

    /// <summary>Written from the CPU after it is made, see <see cref="GraphicsDevice.UploadTexels"/>.</summary>
    Upload = 8
}

/// <summary>How a sampler reads a texture, see <see cref="GraphicsDevice.CreateSampler"/>.</summary>
/// <param name="Smooth">Blended between the texels, nearest texel otherwise.</param>
/// <param name="Mipmaps">Read from the mip levels when drawn smaller, for a texture that has them.</param>
/// <param name="Repeat">Wrap around at the edges, clamp to them otherwise.</param>
/// <param name="Border">Black outside of the edges instead of the edge texel, for a picture that must not smear past its own edge.</param>
public readonly record struct SamplerSettings(bool Smooth, bool Mipmaps = false, bool Repeat = false, bool Border = false);

/// <summary>
/// The things the engine can do with the GPU that depend on the machine. Everything here is Vulkan, but not every
/// card has a compute queue of its own or a transfer queue, so whatever offers one of these asks
/// <see cref="GraphicsDevice.Supports"/> first and makes do without when the answer is no.
/// </summary>
public enum GraphicsFeature
{
    /// <summary>The path traced lighting of a <see cref="Horizon.Rendering.DeferredRenderer2D"/>, done in compute. Wants storage images of the formats it uses.</summary>
    PathTracedLighting,

    /// <summary>Compute that runs on a queue of its own, alongside the drawing of the next frame.</summary>
    AsyncCompute,

    /// <summary>Textures uploaded on a transfer queue of their own, off the render thread.</summary>
    AsyncUploads,

    /// <summary>Timestamps on the GPU, for how long a frame took there.</summary>
    GpuTimer
}
