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
    private const string UNIFORM_SHARP_RECT = "uSharpRect";
    private const string UNIFORM_TINT = "uTint";
    private const string UNIFORM_TEXEL = "uTexel";
    private const string UNIFORM_RADIUS = "uRadius";
    private const string UNIFORM_OPACITY = "uOpacity";
    private const string UNIFORM_SHARE = "uShare";
    private const string UNIFORM_REFRACTION = "uRefraction";
    private const string UNIFORM_BEVEL_DEPTH = "uBevelDepth";
    private const string UNIFORM_BEVEL_WIDTH = "uBevelWidth";
    private const string UNIFORM_MAGNIFY = "uMagnify";
    private const string UNIFORM_ABERRATION = "uAberration";

    // The panes of a UI that keep their blur from one frame to the next, keyed by spatial backdrop hash
    private const int KEPT = 16;
    private const long EVICT_AFTER_FRAMES = 120;

    private readonly GaussianBlur gaussian = new();
    private PostTechnique? copy, glass;
    private PostTarget? sharp, smeared;
    private long sharpLastUsedFrame, smearedLastUsedFrame;

    // The blur and sharp backdrop selected by the latest Capture, and how far it smeared, in pixels
    private PostTarget? current;
    private PostTarget? currentSharp;
    private Vector2 currentSharpOrigin;
    private float blurred = -1.0f;

    // Keyed spatial blur cache slots, with cropped sharp backdrops and LRU/idle VRAM eviction
    private readonly PostTarget?[] kept = new PostTarget?[KEPT];
    private readonly PostTarget?[] keptSharp = new PostTarget?[KEPT];
    private readonly Vector2[] keptSharpOrigin = new Vector2[KEPT];
    private readonly bool[] keptBlurValid = new bool[KEPT];
    private readonly bool[] keptSharpValid = new bool[KEPT];
    private readonly ulong[] keptOf = new ulong[KEPT];
    private readonly long[] lastUsedFrame = new long[KEPT];
    private long frameCounter;

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
        currentSharp = null;
        currentSharpOrigin = Vector2.Zero;

        frameCounter++;
        for (int s = 0; s < KEPT; s++)
        {
            if ((kept[s] is not null || keptSharp[s] is not null) && frameCounter - lastUsedFrame[s] > EVICT_AFTER_FRAMES)
            {
                kept[s]?.Dispose();
                keptSharp[s]?.Dispose();
                kept[s] = null;
                keptSharp[s] = null;
                keptSharpOrigin[s] = Vector2.Zero;
                keptBlurValid[s] = false;
                keptSharpValid[s] = false;
                keptOf[s] = 0;
            }
        }

        if (sharp is not null && frameCounter - sharpLastUsedFrame > EVICT_AFTER_FRAMES)
        {
            sharp.Dispose();
            sharp = null;
        }

        if (smeared is not null && frameCounter - smearedLastUsedFrame > EVICT_AFTER_FRAMES)
        {
            smeared.Dispose();
            smeared = null;
        }
    }

    /// <summary>Forgets every blur that was kept, for when what is behind the UI changed without the UI knowing.</summary>
    public void Forget()
    {
        Array.Clear(keptOf);
        Array.Clear(keptBlurValid);
        Array.Clear(keptSharpValid);
    }

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
    /// Captures and blurs what is behind a pane (or reuses a cached/shared blur). Restores the render target and render
    /// state afterwards so lifted shadow quads in <c>[Under .. Start)</c> can be drawn before <see cref="Composite"/>.
    /// </summary>
    /// <returns>True when a fresh blur was computed or a cached slot was selected; false when reusing the previous pane's blur.</returns>
    public bool Capture(in UIGlassPane pane, ulong madeOf, (int X, int Y, int W, int H) region, Camera camera, Renderer2D? output, RenderTarget? layer, bool fresh)
    {
        Vector2 size = Renderer2D.OutputSize(output);
        if (size.X < 1.0f || size.Y < 1.0f || pane.Opacity <= 0.0f || region.W <= 0 || region.H <= 0)
            return false;

        // Fold skin and glyph atlas generations into the backdrop key so atlas reallocations invalidate cached blurs.
        if (madeOf != 0)
        {
            madeOf = UIGlass.MixBackdropGenerations(
                madeOf,
                Horizon.UI.Skinning.UISkin.AtlasGeneration,
                Horizon.Rendering.Text.GlyphAtlas.Shared.Generation,
                Horizon.Rendering.Text.GlyphAtlas.Subpixel.Generation);
        }

        // Kept for as long as the UI is, whichever scene is drawing right now
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        float pixelsPerUnit = PixelsPerUnit(camera, size);
        float radius = MathF.Max(0.0f, pane.Blur) * pixelsPerUnit;
        float share = GaussianBlur.Share(radius);
        bool blurNeeded = share > 0.001f;
        bool needsSharp = share < 0.999f;
        uint halfWidth = (uint)MathF.Max(1.0f, size.X / 2.0f), halfHeight = (uint)MathF.Max(1.0f, size.Y / 2.0f);

        if (madeOf != 0)
        {
            for (int s = 0; s < KEPT; s++)
            {
                if (keptOf[s] == madeOf
                    && (!blurNeeded || (keptBlurValid[s] && kept[s] is { } had && had.Fits(halfWidth, halfHeight)))
                    && (!needsSharp || (keptSharpValid[s] && keptSharp[s] is { } hadSharp && hadSharp.Fits((uint)region.W, (uint)region.H))))
                {
                    lastUsedFrame[s] = frameCounter;
                    current = blurNeeded ? kept[s] : null;
                    currentSharp = needsSharp ? keptSharp[s] : null;
                    currentSharpOrigin = needsSharp ? keptSharpOrigin[s] : Vector2.Zero;
                    blurred = radius;
                    keptNow++;
                    return true;
                }
            }
        }

        if (!fresh && blurred == radius && (!blurNeeded || current is not null) && (!needsSharp || currentSharp is not null))
            return false;

        var before = RenderState.Save();
        RenderState.DepthTest = false;

        try
        {
            CaptureSharp(output, layer, size, region);

            if (madeOf != 0)
            {
                uint rw = (uint)Math.Max(1, region.W), rh = (uint)Math.Max(1, region.H);
                int slot = ChooseSlot(madeOf, rw, rh, needsSharp);
                keptOf[slot] = madeOf;
                lastUsedFrame[slot] = frameCounter;

                if (blurNeeded)
                {
                    gaussian.Blur(sharp!.Texture, sharp.Size, radius, ref kept[slot], region);
                    keptBlurValid[slot] = true;
                    current = kept[slot];
                }
                else
                {
                    keptBlurValid[slot] = false;
                    current = null;
                }

                if (needsSharp)
                {
                    if (keptSharp[slot] is null || !keptSharp[slot]!.Fits(rw, rh))
                    {
                        keptSharp[slot]?.Dispose();
                        keptSharp[slot] = new PostTarget(rw, rh);
                    }
                    GameEngine.Instance.Graphics.CopyTexels(sharp!.Texture, region.X, region.Y, keptSharp[slot]!.Texture, 0, 0, rw, rh);
                    keptSharpOrigin[slot] = new Vector2(region.X, region.Y);
                    keptSharpValid[slot] = true;
                    currentSharp = keptSharp[slot];
                    currentSharpOrigin = keptSharpOrigin[slot];
                }
                else
                {
                    keptSharpValid[slot] = false;
                    currentSharp = null;
                    currentSharpOrigin = Vector2.Zero;
                }
            }
            else
            {
                if (blurNeeded)
                {
                    gaussian.Blur(sharp!.Texture, sharp.Size, radius, ref smeared, region);
                    smearedLastUsedFrame = frameCounter;
                    current = smeared;
                }
                else
                {
                    current = null;
                }

                currentSharp = needsSharp ? sharp : null;
                currentSharpOrigin = Vector2.Zero;
            }

            blurred = radius;
            madeNow++;
        }
        finally
        {
            // Back to where the UI goes so lifted quads and Composite draw into the right target
            if (layer is not null)
                layer.Bind();
            else
                Renderer2D.BindOutput(output);

            RenderState.Restore(before);
        }

        return true;
    }

    /// <summary>
    /// Composites the pane using the blur and sharp backdrop selected by <see cref="Capture"/>, hardware-scissored to
    /// the pane's visible bounds.
    /// </summary>
    public void Composite(in UIGlassPane pane, Camera camera, Renderer2D? output, RenderTarget? layer)
    {
        Vector2 size = Renderer2D.OutputSize(output);
        if (size.X < 1.0f || size.Y < 1.0f || pane.Opacity <= 0.0f || (current is null && currentSharp is null))
            return;

        var (rect, clip) = Pixels(pane, camera, size);
        Vector4 cut = new(MathF.Max(clip.X, -1.0f), MathF.Max(clip.Y, -1.0f), MathF.Min(clip.Z, size.X + 1.0f), MathF.Min(clip.W, size.Y + 1.0f));

        int sx0 = Math.Clamp((int)MathF.Floor(MathF.Max(rect.X, cut.X)) - 1, 0, (int)size.X);
        int sy0 = Math.Clamp((int)MathF.Floor(MathF.Max(rect.Y, cut.Y)) - 1, 0, (int)size.Y);
        int sx1 = Math.Clamp((int)MathF.Ceiling(MathF.Min(rect.Z, cut.Z)) + 1, sx0, (int)size.X);
        int sy1 = Math.Clamp((int)MathF.Ceiling(MathF.Min(rect.W, cut.W)) + 1, sy0, (int)size.Y);
        if (sx1 <= sx0 || sy1 <= sy0)
            return;

        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        var before = RenderState.Save();
        RenderState.DepthTest = false;
        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;

        if (layer is not null)
            layer.Bind();
        else
            Renderer2D.BindOutput(output);

        float pixelsPerUnit = PixelsPerUnit(camera, size);
        float radius = MathF.Max(0.0f, pane.Blur) * pixelsPerUnit;
        float minBox = MathF.Min(rect.Z - rect.X, rect.W - rect.Y);
        UIGlassOptics optics = UIGlassOptics.Compute(pane.Bend * pixelsPerUnit, pane.Lens, minBox);

        PostTarget blurSource = current ?? currentSharp!;
        PostTarget sharpSource = currentSharp ?? current!;
        Vector4 sharpRect = currentSharp is not null
            ? new Vector4(currentSharpOrigin.X, currentSharpOrigin.Y, currentSharp.Size.X, currentSharp.Size.Y)
            : new Vector4(0.0f, 0.0f, size.X, size.Y);

        uint packedTint = pane.Tint;
        Vector4 tint = new(
            (packedTint & 0xFFu) * (1.0f / 255.0f),
            ((packedTint >> 8) & 0xFFu) * (1.0f / 255.0f),
            ((packedTint >> 16) & 0xFFu) * (1.0f / 255.0f),
            (packedTint >> 24) * (1.0f / 255.0f));

        GraphicsDevice device = GraphicsDevice.Current;
        glass ??= new PostTechnique("glass");
        glass.Bind();
        blurSource.Texture.Bind(0);
        sharpSource.Texture.Bind(1);
        glass.SetUniform(UNIFORM_RECT, in rect);
        glass.SetUniform(UNIFORM_CLIP, in cut);
        glass.SetUniform(UNIFORM_SHARP_RECT, in sharpRect);
        glass.SetUniform(UNIFORM_TINT, in tint);
        Vector2 texel = Vector2.One / size;
        glass.SetUniform(UNIFORM_TEXEL, in texel);
        glass.SetUniform(UNIFORM_RADIUS, pane.Radius * pixelsPerUnit);
        glass.SetUniform(UNIFORM_OPACITY, Math.Clamp(pane.Opacity, 0.0f, 1.0f));
        glass.SetUniform(UNIFORM_SHARE, GaussianBlur.Share(radius));
        glass.SetUniform(UNIFORM_REFRACTION, optics.Refraction);
        glass.SetUniform(UNIFORM_BEVEL_DEPTH, optics.BevelDepth);
        glass.SetUniform(UNIFORM_BEVEL_WIDTH, optics.BevelWidth);
        glass.SetUniform(UNIFORM_MAGNIFY, optics.Magnify);
        glass.SetUniform(UNIFORM_ABERRATION, optics.Aberration);

        try
        {
            device.SetScissor(sx0, sy0, (uint)(sx1 - sx0), (uint)(sy1 - sy0));
            ScreenTriangle.Draw();
        }
        finally
        {
            device.ResetScissor();
            glass.Unbind();
            RenderState.Restore(before);
        }
    }

    private int ChooseSlot(ulong madeOf, uint rw, uint rh, bool needsSharp)
    {
        for (int s = 0; s < KEPT; s++)
        {
            if (keptOf[s] == madeOf)
                return s;
        }

        // During slider drags (blur/bend changing every frame across multiple differently-sized glass panes),
        // prefer an unused slot first, then an older slot whose sharp PostTarget already matches (rw, rh) so
        // panes do not evict each other's dimensions and destroy/recreate Vulkan RenderTargets every frame.
        int empty = -1;
        int sizedLru = -1;
        long sizedOldest = long.MaxValue;
        int lru = 0;
        long oldest = long.MaxValue;
        for (int s = 0; s < KEPT; s++)
        {
            if (keptOf[s] == 0 || (kept[s] is null && keptSharp[s] is null))
            {
                if (needsSharp && keptSharp[s] is { } freeSharp && freeSharp.Fits(rw, rh))
                    return s;
                if (empty < 0)
                    empty = s;
                continue;
            }

            long used = lastUsedFrame[s];
            if (needsSharp && used < frameCounter && keptSharp[s] is { } existing && existing.Fits(rw, rh) && used < sizedOldest)
            {
                sizedOldest = used;
                sizedLru = s;
            }
            if (used < oldest)
            {
                oldest = used;
                lru = s;
            }
        }

        if (empty >= 0)
            return empty;
        if (sizedLru >= 0)
            return sizedLru;
        return lru;
    }

    /// <summary>
    /// Helper method to copy what is drawn so far, the frame and the UI's layer over it, into <see cref="sharp"/>
    /// within <paramref name="region"/>.
    /// </summary>
    private void CaptureSharp(Renderer2D? output, RenderTarget? layer, Vector2 size, (int X, int Y, int W, int H) region)
    {
        if (region.W <= 0 || region.H <= 0)
            return;

        uint width = (uint)size.X, height = (uint)size.Y;
        if (sharp is null || !sharp.Fits(width, height))
        {
            sharp?.Dispose();
            sharp = new PostTarget(width, height);
        }
        sharpLastUsedFrame = frameCounter;

        GraphicsDevice device = GraphicsDevice.Current;
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
            try
            {
                device.SetScissor(region.X, region.Y, (uint)region.W, (uint)region.H);
                ScreenTriangle.Draw();
            }
            finally
            {
                device.ResetScissor();
                copy.Unbind();
            }
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
            try
            {
                device.SetScissor(region.X, region.Y, (uint)region.W, (uint)region.H);
                ScreenTriangle.Draw();
            }
            finally
            {
                device.ResetScissor();
                copy.Unbind();
            }

            RenderState.Blend = false;
        }
    }

    public void Dispose()
    {
        gaussian.Dispose();
        sharp?.Dispose();
        smeared?.Dispose();
        sharp = smeared = current = currentSharp = null;
        currentSharpOrigin = Vector2.Zero;
        for (int i = 0; i < KEPT; i++)
        {
            kept[i]?.Dispose();
            keptSharp[i]?.Dispose();
            kept[i] = null;
            keptSharp[i] = null;
            keptSharpOrigin[i] = Vector2.Zero;
            keptBlurValid[i] = false;
            keptSharpValid[i] = false;
            keptOf[i] = 0;
            lastUsedFrame[i] = 0;
        }
    }
}
