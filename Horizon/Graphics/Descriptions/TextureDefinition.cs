namespace Horizon.Graphics;

/// <summary>
/// What kind of texture to make, what its texels are, how it is read, and what it is for. The presets cover what the
/// engine itself makes, a texture that wants something else is made with a definition of its own.
/// </summary>
public readonly record struct TextureDefinition(
    PixelFormat Format,
    bool Smooth = true,
    bool Repeat = false,
    bool Border = false,
    bool Mipmaps = false,
    TextureUsage Usage = TextureUsage.Sampled | TextureUsage.Upload)
{
    /// <summary>Colours, blended between the texels when drawn bigger or smaller than they are.</summary>
    public static TextureDefinition RgbaUnsignedByte { get; } = new(PixelFormat.Rgba8, Smooth: true);

    /// <summary>Colours, every screen pixel takes the nearest texel. Pixel art.</summary>
    public static TextureDefinition RgbaUnsignedByteNearest { get; } = new(PixelFormat.Rgba8, Smooth: false);

    /// <summary>A single byte per texel, read back exactly as it was written. For grids of data rather than images.</summary>
    public static TextureDefinition RedUnsignedByteNearest { get; } = new(PixelFormat.R8, Smooth: false);

    /// <summary>Sixteen bits a channel, blended between the texels. For a picture that is blended into itself frame after frame.</summary>
    public static TextureDefinition Rgba16Float { get; } = new(PixelFormat.Rgba16F, Smooth: true);

    /// <summary>One half a texel, blended between the texels, written by compute. A field of distances.</summary>
    public static TextureDefinition DistanceField { get; } = new(PixelFormat.R16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    /// <summary>Four floats a texel, read back exactly. For data.</summary>
    public static TextureDefinition Rgba32Float { get; } = new(PixelFormat.Rgba32F, Smooth: false);

    /// <summary>Colours taken to be sRGB and handed over in linear light, black outside of the edges.</summary>
    public static TextureDefinition SrgbLinearLight { get; } = new(PixelFormat.Rgba8Srgb, Smooth: true, Border: true);

    /// <summary>Depth and stencil, for the render targets of everything that cuts sprites out with a mask.</summary>
    public static TextureDefinition DepthStencil { get; } = new(PixelFormat.Depth24Stencil8, Smooth: false, Usage: TextureUsage.RenderTarget);

    /// <summary>Depth on its own.</summary>
    public static TextureDefinition DepthComponent { get; } = new(PixelFormat.Depth32F, Smooth: false, Usage: TextureUsage.RenderTarget);

    /// <summary>The same definition, made to be drawn into as well.</summary>
    public TextureDefinition AsRenderTarget() => this with { Usage = Usage | TextureUsage.RenderTarget };

    /// <summary>The same definition, made to be written by compute shaders as an image as well.</summary>
    public TextureDefinition AsStorage() => this with { Usage = Usage | TextureUsage.Storage };

    /// <summary>Whether the format holds depth (and maybe stencil) rather than colour.</summary>
    public bool IsDepth => Format is PixelFormat.Depth24Stencil8 or PixelFormat.Depth32F;

    /// <summary>Whether the format has a stencil in it.</summary>
    public bool HasStencil => Format == PixelFormat.Depth24Stencil8;

    /// <summary>How many bytes one texel takes.</summary>
    public int BytesPerTexel => Format switch
    {
        PixelFormat.R8 => 1,
        PixelFormat.R16F => 2,
        PixelFormat.Rg16F => 4,
        PixelFormat.Rg32F => 8,
        PixelFormat.Rg32Uint => 8,
        PixelFormat.Rgba16F => 8,
        PixelFormat.Rgba32F => 16,
        _ => 4
    };

    /// <summary>What a sampler reads this texture like, when nobody gives it one of its own.</summary>
    public SamplerSettings Sampler => new(Smooth, Mipmaps, Repeat, Border);
}
