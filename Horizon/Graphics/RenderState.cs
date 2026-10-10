namespace Horizon.Graphics;

/// <summary>
/// How what is drawn gets mixed with what is there already. The first two are for the colour, the other two for the alpha.
/// </summary>
public readonly record struct BlendMode(
    BlendFactor SourceColor,
    BlendFactor DestinationColor,
    BlendFactor SourceAlpha,
    BlendFactor DestinationAlpha)
{
    /// <summary>The same pair for the colour and the alpha.</summary>
    public BlendMode(BlendFactor source, BlendFactor destination) : this(source, destination, source, destination) { }

    /// <summary>See-through things laid over what is there. The alpha that comes out of it is how much of the pixel is covered.</summary>
    public static BlendMode Alpha { get; } = new(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);

    /// <summary>The same for a picture whose colours have been multiplied by its alpha already, which is what comes out of blending with <see cref="Alpha"/>.</summary>
    public static BlendMode Premultiplied { get; } = new(BlendFactor.One, BlendFactor.OneMinusSrcAlpha);

    /// <summary>Light on top of light, nothing gets darker.</summary>
    public static BlendMode Additive { get; } = new(BlendFactor.SrcAlpha, BlendFactor.One, BlendFactor.One, BlendFactor.One);

    /// <summary>The same as not blending at all.</summary>
    public static BlendMode Replace { get; } = new(BlendFactor.One, BlendFactor.Zero);
}

/// <summary>
/// What the GPU is set to blend and test, as it was last set through here. Everything that draws wants these its own
/// way and has to leave them the way it found them.
/// <code>
/// var before = RenderState.Save();
/// RenderState.Blend = true;
/// RenderState.BlendMode = BlendMode.Alpha;
/// // draw
/// RenderState.Restore(before);
/// </code>
/// Render thread only. Nothing of it touches the GPU until something is drawn.
/// </summary>
public static class RenderState
{
    /// <summary>Everything this class keeps, to hand back to <see cref="Restore"/>.</summary>
    public readonly record struct Saved(bool Blend, bool DepthTest, BlendMode BlendMode, bool DepthWrite, CullMode Cull);

    private static bool blend, depthTest, depthWrite = true;
    private static BlendMode blendMode = BlendMode.Replace;
    private static CullMode cull;

    /// <summary>Whether what is drawn writes its depth as well as reading it. On unless told otherwise, glass turns it off.</summary>
    public static bool DepthWrite
    {
        get => depthWrite;
        set
        {
            depthWrite = value;
            GraphicsDevice.Current.SetDepthWrite(value);
        }
    }

    /// <summary>Which side of every triangle is thrown away, none for anything flat, see <see cref="CullMode"/>.</summary>
    public static CullMode Cull
    {
        get => cull;
        set
        {
            cull = value;
            GraphicsDevice.Current.SetCullMode(value);
        }
    }

    /// <summary>Whether what is drawn is mixed with what is there (see <see cref="BlendMode"/>) or simply replaces it.</summary>
    public static bool Blend
    {
        get => blend;
        set
        {
            blend = value;
            GraphicsDevice.Current.SetBlend(blend, in blendMode);
        }
    }

    /// <summary>Whether what is drawn is thrown out where something nearer has been drawn already.</summary>
    public static bool DepthTest
    {
        get => depthTest;
        set
        {
            depthTest = value;
            GraphicsDevice.Current.SetDepthTest(value);
        }
    }

    public static BlendMode BlendMode
    {
        get => blendMode;
        set
        {
            blendMode = value;
            GraphicsDevice.Current.SetBlend(blend, in blendMode);
        }
    }

    public static Saved Save() => new(blend, depthTest, blendMode, depthWrite, cull);

    public static void Restore(in Saved saved)
    {
        blendMode = saved.BlendMode;
        Blend = saved.Blend;
        DepthTest = saved.DepthTest;
        DepthWrite = saved.DepthWrite;
        Cull = saved.Cull;
    }
}
