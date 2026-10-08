using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL;

/// <summary>
/// How what is drawn gets mixed with what is there already. The first two are for the colour, the other two for the alpha.
/// </summary>
public readonly record struct BlendMode(
    BlendingFactor SourceColor,
    BlendingFactor DestinationColor,
    BlendingFactor SourceAlpha,
    BlendingFactor DestinationAlpha)
{
    /// <summary>
    /// The same pair for the colour and the alpha.
    /// </summary>
    public BlendMode(BlendingFactor source, BlendingFactor destination) : this(source, destination, source, destination) { }

    /// <summary>
    /// See-through things laid over what is there. The alpha that comes out of it is how much of the pixel is covered.
    /// </summary>
    public static BlendMode Alpha { get; } = new(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

    /// <summary>
    /// The same for a picture whose colours have been multiplied by its alpha already, which is what comes out of blending with <see cref="Alpha"/>.
    /// </summary>
    public static BlendMode Premultiplied { get; } = new(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

    /// <summary>
    /// Light on top of light, nothing gets darker.
    /// </summary>
    public static BlendMode Additive { get; } = new(BlendingFactor.SrcAlpha, BlendingFactor.One, BlendingFactor.One, BlendingFactor.One);

    /// <summary>
    /// What OpenGL starts out with, which is the same as not blending at all.
    /// </summary>
    public static BlendMode Replace { get; } = new(BlendingFactor.One, BlendingFactor.Zero);
}

/// <summary>
/// What the GPU is set to blend and test, as it was last set through here.
/// <para>
/// Everything that draws wants these its own way and has to leave them the way it found them. Asking OpenGL what they are
/// makes the driver stop and catch up with everything it was told before, every single time. So they are set through here,
/// and whoever wants to know what they are (or put them back) is told from memory.
/// </para>
/// <code>
/// var before = RenderState.Save();
/// RenderState.Blend = true;
/// RenderState.BlendMode = BlendMode.Alpha;
/// // draw
/// RenderState.Restore(before);
/// </code>
/// Render thread only. Something that sets these straight on the GL behind its back is not noticed, and gets overwritten by the next restore.
/// </summary>
public static class RenderState
{
    /// <summary>
    /// Everything this class keeps, to hand back to <see cref="Restore"/>.
    /// </summary>
    public readonly record struct Saved(bool Blend, bool DepthTest, BlendMode BlendMode);

    private static bool blend, depthTest;
    private static BlendMode blendMode = BlendMode.Replace;

    /// <summary>
    /// Whether what is drawn is mixed with what is there (see <see cref="BlendMode"/>) or simply replaces it.
    /// </summary>
    public static bool Blend
    {
        get => blend;
        set
        {
            blend = value;
            Toggle(EnableCap.Blend, value);
        }
    }

    /// <summary>
    /// Whether what is drawn is thrown out where something nearer has been drawn already.
    /// </summary>
    public static bool DepthTest
    {
        get => depthTest;
        set
        {
            depthTest = value;
            Toggle(EnableCap.DepthTest, value);
        }
    }

    public static BlendMode BlendMode
    {
        get => blendMode;
        set
        {
            blendMode = value;
            ObjectManager.GL.BlendFuncSeparate(value.SourceColor, value.DestinationColor, value.SourceAlpha, value.DestinationAlpha);
        }
    }

    public static Saved Save() => new(blend, depthTest, blendMode);

    public static void Restore(in Saved saved)
    {
        Blend = saved.Blend;
        DepthTest = saved.DepthTest;
        BlendMode = saved.BlendMode;
    }

    private static void Toggle(EnableCap what, bool on)
    {
        if (on) ObjectManager.GL.Enable(what);
        else ObjectManager.GL.Disable(what);
    }
}
