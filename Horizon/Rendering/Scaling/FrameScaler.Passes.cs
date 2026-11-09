using System.Numerics;

using Horizon.Graphics;
using Horizon.Logging;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Scaling;

/// <summary>The passes that blow the scene up, made the first time each is needed and never at a scale of 1.</summary>
public sealed partial class FrameScaler
{
    /// <summary>What the GPU timings call the pass, see <see cref="GraphicsDevice.GpuScopes"/>.</summary>
    public const string GPU_SCOPE = "upscale";

    private PostTechnique? cas, nearest, bilinear, gsr1, easu, rcas;
    private ISpatialUpscaler? failedCustom;

    /// <summary>Helper method to blow the scene up into the window with whatever the plan says, leaving bound what was.</summary>
    private void Upscale(GraphicsDevice graphics)
    {
        RenderScalePlan now = plan;
        RenderTarget? source = scene;

        RenderTarget? bound = graphics.BoundRenderTarget;
        var before = RenderState.Save();

        graphics.StandInForWindow(null);
        if (source is not { IsValid: true })
        {
            RenderState.Restore(before);
            return;
        }

        using (graphics.BeginGpuScope(GPU_SCOPE))
        {
            // A scene that was cleared and never drawn into is that colour, which it has to be by now to be read
            source.FlushPendingClears(graphics, null);

            graphics.BindWindow();
            RenderState.Blend = false;
            RenderState.DepthTest = false;
            RenderState.DepthWrite = false;

            Texture picture = source.Color;
            Upscaler upscaler = now.Effective;
            bool customDrew = upscaler == Upscaler.Custom && RunCustom(now, picture);
            if (!customDrew)
            {
                // A custom upscaler that fell over is CAS's to do, the way the plan does it, a stretch first if it has to be
                bool prepass = now.HasPrepass;
                if (upscaler == Upscaler.Custom)
                {
                    upscaler = Upscaler.Cas;
                    prepass = !CasConstants.SupportsScaling(now.OutputWidth, now.OutputHeight, now.SceneWidth, now.SceneHeight);
                }

                RenderState.Blend = false;
                RenderState.DepthTest = false;
                RenderState.DepthWrite = false;

                if (RenderScalePlan.HasReach(upscaler) && prepass)
                    DrawWithPrepass(graphics, now, upscaler, picture);
                else
                    DrawPass(upscaler, picture, now.SceneWidth, now.SceneHeight, now.OutputWidth, now.OutputHeight, now.Sharpness);
            }
        }

        // Whoever had the window bound has it again, at full size now, and whoever had a target of their own has that
        graphics.BindRenderTarget(bound);
        RenderState.Restore(before);
    }

    /// <summary>What the GPU timings call the bilinear stretch ahead of CAS or FSR 1, inside the upscale's.</summary>
    public const string PREPASS_SCOPE = "upscale prepass";

    /// <summary>What the GPU timings call FSR 1's first pass, EASU, inside the upscale's. RCAS is the rest of it.</summary>
    public const string EASU_SCOPE = "fsr1 easu";

    // The stretch an upscaler reads when the scene is further from the window than it reaches, made when a plan needs one
    // and EASU's picture at the window's size, which RCAS sharpens, made when FSR 1 runs
    private RenderTarget? prepassTarget, easuTarget;

    /// <summary>
    /// Helper method for CAS or FSR 1 past their reach. The scene is stretched bilinearly to half of the window each
    /// way (see <see cref="RenderScalePlan.CasReach"/>) and the upscaler, inside its limit, takes that to the window.
    /// Leaves the window bound.
    /// </summary>
    private void DrawWithPrepass(GraphicsDevice graphics, RenderScalePlan now, Upscaler upscaler, Texture picture)
    {
        var (width, height) = now.HasPrepass ? (now.PrepassWidth, now.PrepassHeight) : RenderScalePlan.CasReach(now.OutputWidth, now.OutputHeight, now.SceneWidth, now.SceneHeight);
        if (!FitPrepass(width, height))
        {
            graphics.BindWindow();
            DrawPass(Upscaler.Bilinear, picture, now.SceneWidth, now.SceneHeight, now.OutputWidth, now.OutputHeight, now.Sharpness);
            return;
        }

        using (graphics.BeginGpuScope(PREPASS_SCOPE))
        {
            prepassTarget!.Bind();
            DrawPass(Upscaler.Bilinear, picture, now.SceneWidth, now.SceneHeight, width, height, now.Sharpness);
        }

        graphics.BindWindow();
        DrawPass(upscaler, prepassTarget!.Color, width, height, now.OutputWidth, now.OutputHeight, now.Sharpness);
    }

    /// <summary>Helper method to have the stretch's target a size, made anew when it changes.</summary>
    private bool FitPrepass(uint width, uint height) => Fit(ref prepassTarget, width, height, "upscale prepass");

    /// <summary>Helper method to have a target of the passes a size, made anew when it changes, false (and said) if it can't be had.</summary>
    private static bool Fit(ref RenderTarget? target, uint width, uint height, string name)
    {
        if (target is { IsValid: true } && target.Width == width && target.Height == height)
            return true;

        target?.Dispose();
        target = null;
        using var global = Horizon.Content.AssetScope.EnterGlobal();
        bool made = Engine.GameEngine.Instance.ObjectManager.RenderTargets.TryCreate(
            RenderTargetDescription.Color(width, height, TextureDefinition.RgbaUnsignedByte),
            out var result);
        if (!made)
        {
            Log.Error($"[RenderScale] The {name} target of {width} by {height} couldn't be made, bilinear does it instead: {result.Message}");
            return false;
        }

        target = result.Asset;
        target.Name = name;
        return true;
    }

    private void ReleasePrepass()
    {
        prepassTarget?.Dispose();
        prepassTarget = null;
    }

    private void ReleaseEasu()
    {
        easuTarget?.Dispose();
        easuTarget = null;
    }

    /// <summary>
    /// Draws one of the engine's own upscalers from a picture into whatever is bound, over all of it. What the frame
    /// uses, and what the GPU check holds to the CPU reference. Render thread.
    /// </summary>
    internal void DrawPass(Upscaler upscaler, Texture picture, uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight, float sharpness)
    {
        // Also guard direct passes and a custom upscaler's runtime failure path. Never bind an invalid shader.
        if (!EnsureAvailable(upscaler))
        {
            upscaler = CasConstants.SupportsScaling(outputWidth, outputHeight, sceneWidth, sceneHeight) && EnsureAvailable(Upscaler.Cas)
                ? Upscaler.Cas : Upscaler.Bilinear;
            if (!EnsureAvailable(upscaler)) return;
        }
        switch (upscaler)
        {
            case Upscaler.Nearest:
                DrawNearest(picture, sceneWidth, sceneHeight, outputWidth, outputHeight);
                break;
            case Upscaler.Bilinear:
                DrawBilinear(picture);
                break;
            case Upscaler.Gsr1:
                DrawGsr1(picture, sceneWidth, sceneHeight, sharpness);
                break;
            case Upscaler.Fsr1:
                DrawFsr1(picture, sceneWidth, sceneHeight, outputWidth, outputHeight, sharpness);
                break;
            default:
                DrawCas(picture, sceneWidth, sceneHeight, outputWidth, outputHeight, sharpness);
                break;
        }
    }

    private void DrawCas(Texture picture, uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight, float sharpness)
    {
        cas ??= new PostTechnique("cas");
        CasConstants constants = CasConstants.For(sharpness, sceneWidth, sceneHeight, outputWidth, outputHeight);

        cas.Bind();
        picture.Bind(0);
        cas.SetUniform("uScaleOffset", new Vector4(constants.Scale, constants.Offset.X, constants.Offset.Y));
        cas.SetUniform("uPeak", constants.Peak);
        cas.SetUniform("uSceneSize", new Vector2(sceneWidth, sceneHeight));
        ScreenTriangle.Draw();
        cas.Unbind();
    }

    private void DrawNearest(Texture picture, uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight)
    {
        nearest ??= new PostTechnique("upscale_nearest");

        nearest.Bind();
        picture.Bind(0);
        nearest.SetUniform("uScale", new Vector2(sceneWidth / (float)outputWidth, sceneHeight / (float)outputHeight));
        nearest.SetUniform("uSceneSize", new Vector2(sceneWidth, sceneHeight));
        ScreenTriangle.Draw();
        nearest.Unbind();
    }

    private void DrawGsr1(Texture picture, uint sceneWidth, uint sceneHeight, float sharpness)
    {
        gsr1 ??= new PostTechnique("gsr1");

        gsr1.Bind();
        picture.Bind(0);
        gsr1.SetUniform("uViewportInfo", GsrConstants.ViewportInfo(sceneWidth, sceneHeight));
        gsr1.SetUniform("uEdgeSharpness", SpatialSharpness.GsrEdgeSharpness(sharpness));
        ScreenTriangle.Draw();
        gsr1.Unbind();
    }

    /// <summary>
    /// Helper method for FSR 1, EASU into a picture of the output's size and RCAS from that into whatever was bound.
    /// Without a target for EASU's picture, EASU alone goes straight into what's bound, said once.
    /// </summary>
    private void DrawFsr1(Texture picture, uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight, float sharpness)
    {
        var graphics = GraphicsDevice.Current;
        RenderTarget? bound = graphics.BoundRenderTarget;
        bool rcasToo = Fit(ref easuTarget, outputWidth, outputHeight, "fsr1 easu");

        easu ??= new PostTechnique("fsr1_easu");
        EasuConstants constants = EasuConstants.For(sceneWidth, sceneHeight, outputWidth, outputHeight);

        using (graphics.BeginGpuScope(EASU_SCOPE))
        {
            if (rcasToo)
                easuTarget!.Bind();

            easu.Bind();
            picture.Bind(0);
            easu.SetUniform("uCon0", constants.Con0);
            easu.SetUniform("uCon1", constants.Con1);
            easu.SetUniform("uCon2", constants.Con2);
            easu.SetUniform("uCon3", constants.Con3);
            ScreenTriangle.Draw();
            easu.Unbind();
        }

        if (!rcasToo)
            return;

        graphics.BindRenderTarget(bound);
        rcas ??= new PostTechnique("fsr1_rcas");
        rcas.Bind();
        easuTarget!.Color.Bind(0);
        rcas.SetUniform("uSharpness", SpatialSharpness.RcasAmount(sharpness));
        rcas.SetUniform("uSize", new Vector2(outputWidth, outputHeight));
        ScreenTriangle.Draw();
        rcas.Unbind();
    }

    private void DrawBilinear(Texture picture)
    {
        // The scene's target reads between its pixels by itself, the copy shader is all bilinear takes
        bilinear ??= new PostTechnique("copy");

        bilinear.Bind();
        picture.Bind(0);
        ScreenTriangle.Draw();
        bilinear.Unbind();
    }

    /// <summary>Helper method to have a custom upscaler do it, false (and said once) if it fell over.</summary>
    private bool RunCustom(RenderScalePlan now, Texture picture)
    {
        if (frameCustom is not { } custom)
            return false;

        try
        {
            custom.Upscale(new SpatialUpscale(picture, now.SceneWidth, now.SceneHeight, now.OutputWidth, now.OutputHeight, now.Sharpness));

            // It was asked to leave the window bound, whatever it bound besides
            GraphicsDevice.Current.BindWindow();
            return true;
        }
        catch (Exception e)
        {
            if (!ReferenceEquals(failedCustom, custom))
            {
                failedCustom = custom;
                Log.Error($"[RenderScale] {custom.Name} fell over upscaling, CAS or bilinear does it instead. {e.Message}");
            }

            GraphicsDevice.Current.BindWindow();
            return false;
        }
    }

    // The techniques are shared by name with every other pass of the same shader (see PostTechnique), so they're let
    // go of and never disposed here
    private void DisposePasses()
    {
        ReleasePrepass();
        ReleaseEasu();
        cas = nearest = bilinear = gsr1 = easu = rcas = null;
    }
}
