using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// A picture to draw into and read back, what the passes of the post processing hand to each other. One texture of
/// colour and nothing else, in whatever format and with whatever filter it is asked for.
/// </summary>
public sealed class PostTarget : IDisposable
{
    /// <summary>Colours, blended between the pixels when read at another size. What a picture is usually kept in.</summary>
    public static TextureDefinition Smooth => TextureDefinition.RgbaUnsignedByte;

    /// <summary>Read back exactly as written, for what holds data rather than a picture.</summary>
    public static TextureDefinition Exact => TextureDefinition.RgbaUnsignedByteNearest;

    /// <summary>
    /// Colours that are taken to be in sRGB and handed to whoever reads them in linear light, blended between the
    /// pixels after that. The GPU does the conversion and gets the blending right, which done by hand in a shader
    /// is a power a channel for every read. What is drawn into it goes in as it is. Black outside of its edges.
    /// </summary>
    public static TextureDefinition LinearLight => TextureDefinition.SrgbLinearLight;

    /// <summary>
    /// Sixteen bits a channel instead of eight, for a picture that is blended into itself frame after frame (a trail, a glow that builds up).
    /// Read between its pixels like <see cref="Smooth"/>.
    /// </summary>
    public static TextureDefinition Precise => TextureDefinition.Rgba16Float;

    private readonly RenderTarget frameBuffer;

    public Texture Texture { get; }

    /// <summary>The render target behind it, for clearing it or binding one of its attachments.</summary>
    public RenderTarget FrameBuffer => frameBuffer;

    /// <summary>The size in pixels.</summary>
    public Vector2 Size { get; }

    /// <summary>Has to be made on the render thread.</summary>
    /// <param name="definition">What kind of texture it is, see the ones this class offers. <see cref="Smooth"/> if left out.</param>
    /// <exception cref="Exception">The GPU wouldn't have it.</exception>
    public PostTarget(uint width, uint height, TextureDefinition? definition = null)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        bool created = GameEngine.Instance.ObjectManager.RenderTargets.TryCreate(
            RenderTargetDescription.Color(width, height, definition ?? Smooth),
            out var result);

        if (!created)
            throw new Exception($"A post processing target of {width} by {height} couldn't be made: {result.Message}");

        frameBuffer = result.Asset;
        frameBuffer.Name = "post target";
        Texture = frameBuffer.Color;
        Size = new Vector2(width, height);
    }

    /// <summary>Whether this is the size somebody wants, who would otherwise make another.</summary>
    public bool Fits(uint width, uint height) => (uint)Size.X == Math.Max(1, width) && (uint)Size.Y == Math.Max(1, height);

    /// <summary>Makes this what is drawn into, all of it.</summary>
    public void Bind() => frameBuffer.Bind();

    /// <summary>
    /// Copies what is in the window right now into this target, which is the frame as far as it has been drawn.
    /// It is stretched to fit if the two aren't the same size. Render thread, and whatever is bound stays bound.
    /// </summary>
    public void CopyFromWindow()
    {
        Vector2 window = GameEngine.Instance.WindowManager.ViewportSize;
        GraphicsDevice.Current.CopyWindow(frameBuffer, (uint)window.X, (uint)window.Y, (uint)Size.X, (uint)Size.Y);
    }

    public void Dispose() => frameBuffer.Dispose();
}
