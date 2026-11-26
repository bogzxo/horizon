using System.Numerics;
using System.Reflection;

using Horizon.Rendering;
using Horizon.Rendering.Meshes;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Scaling;
using Horizon.Rendering.Voxels;

namespace Horizon.Tests;

public class Pass2Rendering3DAndCommonTests
{
    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    /* 3D Mesh geometry and disposal */

    [Fact]
    public void Built_in_shapes_produce_valid_in_bounds_geometry_and_dispose_safely_headlessly()
    {
        Mesh3D plane = Mesh3D.Plane(4.0f, 6.0f, 2.0f);
        Assert.NotNull(plane.Pending);
        var (planeVerts, planeIndices) = plane.Pending.Value;
        Assert.Equal(4, planeVerts.Length);
        Assert.Equal(6, planeIndices.Length);
        Assert.All(planeVerts, v => Assert.Equal(Vector3.UnitY, v.Normal));

        Mesh3D cylinder = Mesh3D.Cylinder(0.5f, 2.0f, segments: 2);
        Assert.NotNull(cylinder.Pending);
        var (cylVerts, cylIndices) = cylinder.Pending.Value;
        // segments clamps to 3 -> 4*3 + 6 = 18 vertices, 12*3 = 36 indices
        Assert.Equal(18, cylVerts.Length);
        Assert.Equal(36, cylIndices.Length);
        Assert.All(cylIndices, idx => Assert.True(idx < (uint)cylVerts.Length));

        // Headless disposal (Buffer is null) must clear pending geometry and remain idempotent
        plane.Dispose();
        plane.Dispose();
        Assert.Null(plane.Pending);
        Assert.Equal(0u, plane.TriangleCount);

        cylinder.Dispose();
        Assert.Null(cylinder.Pending);
    }

    /* RenderScalePlan and DensityRenderScale edge cases */

    [Theory]
    [InlineData(RenderScalePolicy.Manual)]
    [InlineData(RenderScalePolicy.Automatic)]
    [InlineData(RenderScalePolicy.Density)]
    public void A_degenerate_zero_width_or_zero_height_window_never_activates_scaling(RenderScalePolicy policy)
    {
        var settings = new RenderScaleSettings { Policy = policy, Scale = 0.5f, ShortEdge = 720 };

        RenderScalePlan zeroHeight = RenderScalePlan.For(settings, 1920, 0, 2.0f, 16384);
        Assert.False(zeroHeight.Active);
        Assert.Equal((1920u, 0u), (zeroHeight.SceneWidth, zeroHeight.SceneHeight));

        RenderScalePlan zeroWidth = RenderScalePlan.For(settings, 0, 1080, 2.0f, 16384);
        Assert.False(zeroWidth.Active);
        Assert.Equal((0u, 1080u), (zeroWidth.SceneWidth, zeroWidth.SceneHeight));
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-2.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NativeFor_sanitizes_invalid_density_so_LogicalSize_stays_finite(float badDensity)
    {
        RenderScalePlan plan = RenderScalePlan.NativeFor(1920, 1080, badDensity);
        Assert.Equal(1.0f, plan.Density);
        var (dpW, dpH) = plan.LogicalSize;
        Assert.Equal(1920.0f, dpW);
        Assert.Equal(1080.0f, dpH);
    }

    [Fact]
    public void CasReach_handles_odd_dimensions_and_uint_max_without_overflow()
    {
        Assert.Equal((961u, 541u), RenderScalePlan.CasReach(1921, 1081, 400, 200));

        var (w, h) = RenderScalePlan.CasReach(uint.MaxValue, uint.MaxValue, 100, 100);
        Assert.Equal((uint.MaxValue / 2) + 1, w);
        Assert.Equal((uint.MaxValue / 2) + 1, h);
    }

    [Fact]
    public void DensityRenderScale_clamps_default_NativeFrom_against_high_Minimum()
    {
        var scale = new DensityRenderScale
        {
            Minimum = 0.98f,
            NativeFrom = float.NaN,
            TargetDensity = 2.0f,
            PixelBudget = 1920.0 * 1080.0
        };

        float picked = scale.ScaleFor(3840, 2160, 4.0f);
        Assert.True(float.IsFinite(picked));
        Assert.InRange(picked, 0.98f, 1.0f);
    }

    /* PostProcessing and BlurredBackdrop CPU state logic */

    [Fact]
    public void BlurredBackdrop_and_post_effects_manage_state_and_dispose_safely_headlessly()
    {
        var backdrop = new BlurredBackdrop { Radius = 12.0f, Dim = 0.4f, Tint = new Vector3(0.1f, 0.2f, 0.3f) };
        Assert.Equal(12.0f, backdrop.Radius);
        Assert.Equal(0.4f, backdrop.Dim);
        backdrop.Dispose();
        backdrop.Dispose();

        using var processor = new PostProcessor();
        var bloom = processor.Add(new BloomEffect { Intensity = 0.0f, Enabled = true });
        var blur = processor.Add(new BlurEffect { Radius = 6.0f, Enabled = false });
        var crt = processor.Add(new CrtEffect { PixelSize = 2.0f, Enabled = false });
        var exposure = processor.Add(new ExposureEffect { Exposure = 1.1f, Enabled = true });

        Assert.True(processor.IsActive);
        Assert.Same(bloom, processor.Get<BloomEffect>());
        Assert.Same(blur, processor.Get<BlurEffect>());
        Assert.Same(crt, processor.Get<CrtEffect>());
        Assert.Same(exposure, processor.Get<ExposureEffect>());
        Assert.True(processor.Prepare());

        bloom.Enabled = false;
        exposure.Enabled = false;
        Assert.False(processor.IsActive);
        Assert.False(processor.Prepare());

        Assert.True(processor.Remove(crt));
        Assert.Null(processor.Get<CrtEffect>());
        crt.Dispose();

        // GaussianBlur CPU math invariants
        Assert.Equal(0.0f, GaussianBlur.Share(0.0f));
        Assert.Equal(1.0f, GaussianBlur.Share(GaussianBlur.FULL_RADIUS));
        Assert.Equal(1.0f, GaussianBlur.Share(20.0f));
        Assert.True(GaussianBlur.Margin(16.0f, new Vector2(1920, 1080)) > GaussianBlur.Margin(4.0f, new Vector2(1920, 1080)));
    }

    /* Zero-closure IL verification for modified hot paths */

    public static TheoryData<Type, string> ModifiedHotPaths => new()
    {
        { typeof(Mesh3D), nameof(Mesh3D.Render) },
        { typeof(Mesh3D), nameof(Mesh3D.Upload) },
        { typeof(Mesh3D), "UploadOwned" },
        { typeof(VoxelRenderer), "BeforeResolve" },
        { typeof(VoxelRenderer), "Clear" },
        { typeof(VoxelRenderer), "RunBloom" },
        { typeof(VoxelRenderer), "BindWorld" },
        { typeof(VoxelRenderer), "AllTechniquesValid" },
        { typeof(VoxelRenderer), "Grow" },
        { typeof(Renderer2D), nameof(Renderer2D.Render) },
        { typeof(Renderer2D), "Resolve" },
        { typeof(BlurredBackdrop), nameof(BlurredBackdrop.Render) },
        { typeof(BloomEffect), "Render" },
        { typeof(BlurEffect), "Render" },
        { typeof(CrtEffect), "Render" },
        { typeof(ExposureEffect), "Render" },
        { typeof(GaussianBlur), "Blur" },
        { typeof(GaussianBlur), "BlurAtQuarter" },
        { typeof(PostProcessor), "Prepare" },
        { typeof(PostProcessor), "Run" },
        { typeof(PostProcessor), "Blit" },
        { typeof(PostContext), nameof(PostContext.Copy) },
        { typeof(PostLayer), nameof(PostLayer.Begin) },
        { typeof(PostLayer), nameof(PostLayer.Replay) },
        { typeof(PostLayer), "Compose" },
        { typeof(FrameScaler), "BeginFrame" },
        { typeof(FrameScaler), "Upscale" },
        { typeof(FrameScaler), "DrawPass" },
        { typeof(FrameScaler), "DrawCas" },
        { typeof(FrameScaler), "DrawFsr1" },
        { typeof(FrameScaler), "DrawGsr1" },
        { typeof(FrameScaler), "DrawNearest" },
        { typeof(FrameScaler), "DrawBilinear" },
    };

    [Theory]
    [MemberData(nameof(ModifiedHotPaths))]
    public void Modified_rendering_hot_paths_allocate_no_closures(Type type, string name)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL).Where(method => method.Name == name)];
        Assert.True(methods.Length > 0, $"{type.Name} has no method called {name}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"{type.Name}.{name} allocates closure {closure}.");
        }
    }

    private static string? ClosureMadeIn(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return null;

        Type[]? ofType = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? ofMethod = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != NEWOBJ)
                continue;

            MethodBase? made;
            try
            {
                made = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), ofType, ofMethod);
            }
            catch (Exception)
            {
                continue;
            }

            if (made?.DeclaringType is { } declared && declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
                return declared.Name;
        }

        return null;
    }
}
