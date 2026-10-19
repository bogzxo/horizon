using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.PostProcessing;

namespace Horizon.UI.Drawing;

/// <summary>
/// Puts the panes of glass of a UI down, see <see cref="UIGlass"/>. What is drawn so far (the scene, and the UI's own
/// layer laid over it when it has one) is copied, blurred with the same blur everything else blurs with, and laid inside
/// the pane where the UI is being drawn, with round corners worked out per pixel. Render thread, between the stretches
/// of a <see cref="UIRenderer"/>.
/// </summary>
internal sealed class UIGlassPass : IDisposable
{
    private const string UNIFORM_RECT = "uRect";
    private const string UNIFORM_CLIP = "uClip";
    private const string UNIFORM_RADIUS = "uRadius";
    private const string UNIFORM_OPACITY = "uOpacity";
    private const string UNIFORM_SHARE = "uShare";
    private const string UNIFORM_BEND = "uBend";
    private const string UNIFORM_TEXEL = "uTexel";

    // The panes of a UI that keep their blur from one frame to the next, the first so many of them, see Draw
    private const int KEPT = 8;

    private readonly GaussianBlur gaussian = new();
    private PostTechnique? copy, glass;
    private PostTarget? sharp, smeared;

    // The blur shown last, and how far it smeared, in pixels
    private PostTarget? current;
    private float blurred = -1.0f;

    // The blur of every pane as it was last made, and what it was made of (see Draw), so a frame that changed nothing
    // under a pane shows the blur it had. Sparks flying over the editor and every letter typed into the palette had
    // every pane copy the screen and blur it again, a few hundred microseconds of a laptop's GPU a frame for nothing
    private readonly PostTarget?[] kept = new PostTarget?[KEPT];
    private readonly ulong[] keptOf = new ulong[KEPT];

    /// <summary>How many of the panes of the last frame were shown with the blur they had, and how many were blurred anew.</summary>
    public (int Kept, int Made) LastFrame { get; private set; }
    private int keptNow, madeNow;

    /// <summary>Forgets the blur that was made last, the next pane makes one of its own. Once a frame, before the first.</summary>
    public void Reset()
    {
        LastFrame = (keptNow, madeNow);
        keptNow = madeNow = 0;
        blurred = -1.0f;
        current = null;
    }

    /// <summary>Forgets every blur that was kept, for when what is behind the UI changed without the UI knowing.</summary>
    public void Forget() => Array.Clear(keptOf);

    /// <summary>
    /// Helper method to say where a pane is in pixels of what is drawn into, from its top left corner, through the
    /// camera the UI is drawn with. Min then max, the pane and then its clip.
    /// </summary>
    public static (Vector4 Rect, Vector4 Clip) Pixels(in UIGlassPane pane, Camera camera, Vector2 size)
    {
        Matrix4x4 toClip = camera.View * camera.Projection;
        return (Box(pane.Min, pane.Max, toClip, size), Box(pane.ClipMin, pane.ClipMax, toClip, size));
    }

    /// <summary>Helper method to say how many pixels of what is drawn into a unit of the camera's world is, across.</summary>
    public static float PixelsPerUnit(Camera camera, Vector2 size) => MathF.Abs(camera.Projection.M11 * camera.View.M11) * size.X * 0.5f;

    private static Vector4 Box(Vector2 min, Vector2 max, in Matrix4x4 toClip, Vector2 size)
    {
        // A side that isn't cut is as far as there is, which stays that way through any camera
        Vector2 a = Pixel(min, toClip, size), b = Pixel(max, toClip, size);
        if (!float.IsFinite(min.X)) (a.X, b.X) = (float.NegativeInfinity, float.PositiveInfinity);
        if (!float.IsFinite(min.Y)) (a.Y, b.Y) = (float.NegativeInfinity, float.PositiveInfinity);
        if (!float.IsFinite(max.X)) (a.X, b.X) = (float.NegativeInfinity, float.PositiveInfinity);
        if (!float.IsFinite(max.Y)) (a.Y, b.Y) = (float.NegativeInfinity, float.PositiveInfinity);

        Vector2 low = Vector2.Min(a, b), high = Vector2.Max(a, b);
        return new Vector4(low.X, low.Y, high.X, high.Y);
    }

    // The camera's world is Y up and the engine's cameras are the OpenGL way round, pixels count down from the top
    private static Vector2 Pixel(Vector2 point, in Matrix4x4 toClip, Vector2 size)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return Vector2.Zero;

        Vector4 clip = Vector4.Transform(new Vector4(point, 0.0f, 1.0f), toClip);
        float w = clip.W == 0.0f ? 1.0f : clip.W;
        return new Vector2((clip.X / w * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / w * 0.5f) * size.Y);
    }

    /// <summary>
    /// Puts a pane down. Whatever was bound to draw the UI into is bound again afterwards and the render state is as it was.
    /// </summary>
    /// <param name="output">What the frame is drawn into under the UI, a renderer or (null) the window.</param>
    /// <param name="layer">The UI's own layer when it is drawn onto one, null when it goes straight over the frame.</param>
    /// <param name="fresh">
    /// Whether the picture has to be copied and blurred anew, false when nothing was drawn near the pane since the last
    /// one blurred it with the same smear (the side bar next to the panel), which then shows the same blur.
    /// </param>
    /// <param name="index">Which pane of the UI it is, counted from the first.</param>
    /// <param name="madeOf">
    /// What is under the pane, a hash of everything drawn before it and of the pane, 0 for don't know. The same as the
    /// last frame's and the blur that was made then is shown again without a copy or a blur.
    /// </param>
    public void Draw(in UIGlassPane pane, int index, ulong madeOf, Camera camera, Renderer2D? output, RenderTarget? layer, bool fresh)
    {
        Vector2 size = Renderer2D.OutputSize(output);
        if (size.X < 1.0f || size.Y < 1.0f || pane.Opacity <= 0.0f)
            return;

        // Kept for as long as the UI is, whichever scene is drawing right now
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        var before = RenderState.Save();
        RenderState.DepthTest = false;

        float radius = pane.Blur * PixelsPerUnit(camera, size);
        uint halfWidth = (uint)MathF.Max(1.0f, size.X / 2.0f), halfHeight = (uint)MathF.Max(1.0f, size.Y / 2.0f);
        bool keeps = index < KEPT;

        if (keeps && madeOf != 0 && keptOf[index] == madeOf && kept[index] is { } had && had.Fits(halfWidth, halfHeight))
        {
            // Nothing under it changed since the last frame
            current = had;
            blurred = radius;
            keptNow++;
        }
        else if (fresh || current is null || blurred != radius || sharp is null || !sharp.Fits((uint)size.X, (uint)size.Y))
        {
            if (keeps)
            {
                Blur(output, layer, size, radius, ref kept[index]);
                current = kept[index];
                keptOf[index] = madeOf;
            }
            else
            {
                Blur(output, layer, size, radius, ref smeared);
                current = smeared;
            }

            blurred = radius;
            madeNow++;
        }
        else if (keeps)
        {
            // Showing another pane's blur, which is that pane's to keep
            keptOf[index] = 0;
        }

        // Back to where the UI goes, and the pane blended into it the way the UI's quads are
        if (layer is not null)
            layer.Bind();
        else
            Renderer2D.BindOutput(output);

        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;

        var (rect, clip) = Pixels(pane, camera, size);
        Vector4 cut = new(MathF.Max(clip.X, -1.0f), MathF.Max(clip.Y, -1.0f), MathF.Min(clip.Z, size.X + 1.0f), MathF.Min(clip.W, size.Y + 1.0f));

        glass ??= new PostTechnique("glass");
        glass.Bind();
        current!.Texture.Bind(0);
        (sharp ?? current).Texture.Bind(1);
        glass.SetUniform(UNIFORM_RECT, in rect);
        glass.SetUniform(UNIFORM_CLIP, in cut);
        glass.SetUniform(UNIFORM_RADIUS, pane.Radius * PixelsPerUnit(camera, size));
        glass.SetUniform(UNIFORM_OPACITY, Math.Clamp(pane.Opacity, 0.0f, 1.0f));
        glass.SetUniform(UNIFORM_SHARE, GaussianBlur.Share(radius));
        glass.SetUniform(UNIFORM_BEND, pane.Bend * PixelsPerUnit(camera, size));
        Vector2 texel = Vector2.One / size;
        glass.SetUniform(UNIFORM_TEXEL, in texel);
        ScreenTriangle.Draw();
        glass.Unbind();

        RenderState.Restore(before);
    }

    /// <summary>
    /// Helper method to copy what is drawn so far, the frame and the UI's layer over it, and blur it.
    /// </summary>
    private void Blur(Renderer2D? output, RenderTarget? layer, Vector2 size, float radius, ref PostTarget? into)
    {
        uint width = (uint)size.X, height = (uint)size.Y;
        if (sharp is null || !sharp.Fits(width, height))
        {
            sharp?.Dispose();
            sharp = new PostTarget(width, height);
        }

        RenderState.Blend = false;
        copy ??= new PostTechnique("copy");

        // What the UI is drawn over
        if (output is null)
        {
            sharp.CopyFromWindow();
        }
        else
        {
            copy.Bind();
            output.FrameBuffer.Color.Bind(0);
            sharp.Bind();
            ScreenTriangle.Draw();
            copy.Unbind();
        }

        // And as much of the UI as went on before the pane, which on a layer is still apart from the frame. Its
        // colours come multiplied by how much of them there is, see PostLayer
        if (layer is not null)
        {
            RenderState.Blend = true;
            RenderState.BlendMode = BlendMode.Premultiplied;

            copy.Bind();
            layer.Color.Bind(0);
            sharp.Bind();
            ScreenTriangle.Draw();
            copy.Unbind();

            RenderState.Blend = false;
        }

        gaussian.Blur(sharp.Texture, sharp.Size, MathF.Max(radius, 0.0f), ref into);
    }

    public void Dispose()
    {
        gaussian.Dispose();
        sharp?.Dispose();
        smeared?.Dispose();
        sharp = smeared = current = null;
        for (int i = 0; i < KEPT; i++)
        {
            kept[i]?.Dispose();
            kept[i] = null;
            keptOf[i] = 0;
        }
    }
}
