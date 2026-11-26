using System.Numerics;
using System.Reflection;

using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles.Simulation;
using Horizon.Rendering.Primitives;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;

namespace Horizon.Tests;

/// <summary>
/// Headless unit tests for Pass 2 2D rendering fixes and optimizations: primitive blend fallback,
/// sprite batch capture fast paths and disposal, TileMap repeating image pan bounds, TextureAtlas
/// strip coalescing, static Technique validity guards, and zero-closure IL verification.
/// </summary>
public class Pass2Rendering2DTests
{
    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    [Fact]
    public void PrimitiveRenderer_BlendShapes_blends_matching_shapes_and_falls_back_to_newer_shape()
    {
        ShapeInstance[] from =
        [
            ShapeInstance.Disc(new Vector2(0.0f, 0.0f), 10.0f, SpriteItem.White),
            ShapeInstance.Disc(new Vector2(10.0f, 10.0f), 5.0f, SpriteItem.White),
            ShapeInstance.Box(new Vector2(0.0f, 0.0f), new Vector2(10.0f, 10.0f), 0.0f, SpriteItem.White),
        ];

        ShapeInstance[] to =
        [
            // Same kind, within teleport distance -> blends
            ShapeInstance.Disc(new Vector2(20.0f, 10.0f), 20.0f, SpriteItem.White),
            // Kind changed from Circle to Rectangle -> cannot blend, must fall back to to[1]
            ShapeInstance.Box(new Vector2(12.0f, 12.0f), new Vector2(8.0f, 8.0f), 0.0f, SpriteItem.White),
            // Teleported > 256 units -> cannot blend, must fall back to to[2]
            ShapeInstance.Box(new Vector2(500.0f, 0.0f), new Vector2(10.0f, 10.0f), 0.0f, SpriteItem.White),
        ];

        ShapeInstance[] into = new ShapeInstance[3];
        PrimitiveRenderer.BlendShapes(from, to, 0.5f, into);

        // Index 0 blended halfway
        Assert.Equal(new Vector2(10.0f, 5.0f), into[0].Center);
        Assert.Equal(new Vector2(15.0f, 15.0f), into[0].Size);

        // Index 1 fell back to newer shape (Rectangle), not old Circle
        Assert.Equal((uint)ShapeKind.Rectangle, into[1].Kind);
        Assert.Equal(to[1].Center, into[1].Center);

        // Index 2 fell back to newer teleported position, not old origin
        Assert.Equal(to[2].Center, into[2].Center);
    }

    [Fact]
    public void SpriteBatch_WriteCapturedUninterpolated_stamps_texture_and_detects_shadow_casters()
    {
        SpriteItem[] current =
        [
            SpriteItem.Rectangle(new Vector2(0, 0), new Vector2(16, 16), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
            SpriteItem.Rectangle(new Vector2(16, 0), new Vector2(32, 16), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture | SpriteItem.ShadowFlag),
        ];

        SpriteItem[] dest = new SpriteItem[2];
        const uint TEXTURE_SLOT = 42u;

        bool hasCasters = SpriteBatch.WriteCapturedUninterpolated(current, dest, TEXTURE_SLOT);

        Assert.True(hasCasters);
        Assert.Equal(TEXTURE_SLOT, SpriteItem.TextureOf(dest[0].Flags));
        Assert.Equal(TEXTURE_SLOT, SpriteItem.TextureOf(dest[1].Flags));
        Assert.NotEqual(0u, dest[1].Flags & SpriteItem.ShadowFlag);
        Assert.Equal(current[0].Origin, dest[0].Origin);
        Assert.Equal(current[1].Origin, dest[1].Origin);

        // Without ShadowFlag, hasCasters must be false
        current[1].Flags = SpriteItem.NoTexture;
        Assert.False(SpriteBatch.WriteCapturedUninterpolated(current, dest, TEXTURE_SLOT));
    }

    [Fact]
    public void SpriteBatch_WriteCapturedBlended_handles_static_moving_snapped_and_new_sprites()
    {
        var staticSprite = new Sprite(new Vector2(16, 16));
        var movingSprite = new Sprite(new Vector2(16, 16));
        var snappedSprite = new Sprite(new Vector2(16, 16));
        var newSprite = new Sprite(new Vector2(16, 16));

        SpriteItem[] prevItems =
        [
            SpriteItem.Rectangle(new Vector2(10, 20), new Vector2(26, 36), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
            SpriteItem.Rectangle(new Vector2(0, 0), new Vector2(16, 16), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
            SpriteItem.Rectangle(new Vector2(5, 5), new Vector2(21, 21), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
        ];
        Sprite[] prevSprites = [staticSprite, movingSprite, snappedSprite];
        int[] prevEpochs = [1, 1, 1];

        SpriteItem[] currItems =
        [
            // Static sprite: identical transform/color/ring -> fast path keeps from
            SpriteItem.Rectangle(new Vector2(10, 20), new Vector2(26, 36), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
            // Moving sprite: blends halfway
            SpriteItem.Rectangle(new Vector2(20, 40), new Vector2(36, 56), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
            // Snapped sprite: epoch changed -> stays at from while alpha < 1
            SpriteItem.Rectangle(new Vector2(50, 50), new Vector2(66, 66), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture),
            // Newly added sprite beyond previous count -> drawn as current
            SpriteItem.Rectangle(new Vector2(100, 100), new Vector2(116, 116), Vector2.Zero, new Vector2(16, 16), SpriteItem.White, SpriteItem.NoTexture | SpriteItem.ShadowFlag),
        ];
        Sprite[] currSprites = [staticSprite, movingSprite, snappedSprite, newSprite];
        int[] currEpochs = [1, 1, 2, 1];

        SpriteItem[] dest = new SpriteItem[4];
        const uint SLOT = 7u;

        bool hasCasters = SpriteBatch.WriteCapturedBlended(
            prevItems, prevSprites, prevEpochs,
            currItems, currSprites, currEpochs,
            dest, 0.5f, SLOT);

        Assert.True(hasCasters);
        Assert.Equal(new Vector2(10, 20), dest[0].Origin);
        Assert.Equal(new Vector2(10, 20), dest[1].Origin);
        Assert.Equal(new Vector2(5, 5), dest[2].Origin);
        Assert.Equal(new Vector2(100, 100), dest[3].Origin);

        for (int i = 0; i < dest.Length; i++)
            Assert.Equal(SLOT, SpriteItem.TextureOf(dest[i].Flags));
    }

    [Fact]
    public void SpriteBatch_overrides_DisposeOther_to_release_GPU_meshes()
    {
        MethodInfo? disposeOther = typeof(SpriteBatch).GetMethod("DisposeOther", ALL);
        Assert.NotNull(disposeOther);
        Assert.Equal(typeof(SpriteBatch), disposeOther!.DeclaringType);
    }

    [Fact]
    public void TileMap_image_bounds_keep_cached_tiles_while_panning_inside_padded_range()
    {
        Vector2 corner = Vector2.Zero;
        Vector2 size = new(64.0f, 64.0f);

        // Initial view covers [0..2] x [0..1]
        TileMap.ComputeImageRange(new Vector2(10.0f, 10.0f), new Vector2(140.0f, 80.0f), corner, size, repeatX: true, repeatY: true,
            out int firstX, out int lastX, out int firstY, out int lastY);
        Assert.Equal((0, 2, 0, 1), (firstX, lastX, firstY, lastY));

        // Padded build covers [-1..3] x [-1..2]
        int cachedFirstX = firstX - 1, cachedLastX = lastX + 1, cachedFirstY = firstY - 1, cachedLastY = lastY + 1;

        // Small pan or screen shake across a tile boundary stays inside cached bounds -> no rebuild
        TileMap.ComputeImageRange(new Vector2(70.0f, -10.0f), new Vector2(200.0f, 60.0f), corner, size, repeatX: true, repeatY: true,
            out int panFirstX, out int panLastX, out int panFirstY, out int panLastY);
        Assert.True(TileMap.CanKeepImageBounds(cachedFirstX, cachedLastX, cachedFirstY, cachedLastY, panFirstX, panLastX, panFirstY, panLastY));

        // Panning past the padded boundary invalidates -> triggers rebuild
        TileMap.ComputeImageRange(new Vector2(140.0f, 10.0f), new Vector2(270.0f, 80.0f), corner, size, repeatX: true, repeatY: true,
            out int farFirstX, out int farLastX, out int farFirstY, out int farLastY);
        Assert.False(TileMap.CanKeepImageBounds(cachedFirstX, cachedLastX, cachedFirstY, cachedLastY, farFirstX, farLastX, farFirstY, farLastY));

        // Zooming way in so cached bounds are vastly oversized invalidates to shed excess tiles
        Assert.False(TileMap.CanKeepImageBounds(-10, 10, -10, 10, 0, 0, 0, 0));
    }

    [Fact]
    public void TextureAtlas_FindCoalescedRunEnd_merges_adjacent_same_shelf_cuts_and_stops_on_gaps()
    {
        byte[] dummy = [];
        TextureAtlas.Cut[] cuts =
        [
            // Shelf y=0, height=32: three adjacent cuts separated by 2px padding -> should coalesce into 1 strip of width 94
            new(0, 0, 30, 32, dummy),
            new(32, 0, 30, 32, dummy),
            new(64, 0, 30, 32, dummy),
            // Gap of 10px (e.g. an older sprite lives between 94 and 104) -> must NOT merge!
            new(104, 0, 30, 32, dummy),
            // Different height on same Y -> must NOT merge!
            new(136, 0, 30, 16, dummy),
            // Different Y -> must NOT merge!
            new(0, 34, 30, 16, dummy),
        ];

        int end0 = TextureAtlas.FindCoalescedRunEnd(cuts, 0, maxGap: 2, out int width0);
        Assert.Equal(3, end0);
        Assert.Equal(94, width0);

        int end1 = TextureAtlas.FindCoalescedRunEnd(cuts, 3, maxGap: 2, out int width1);
        Assert.Equal(4, end1);
        Assert.Equal(30, width1);

        int end2 = TextureAtlas.FindCoalescedRunEnd(cuts, 4, maxGap: 2, out int width2);
        Assert.Equal(5, end2);
        Assert.Equal(30, width2);

        int end3 = TextureAtlas.FindCoalescedRunEnd(cuts, 5, maxGap: 2, out int width3);
        Assert.Equal(6, end3);
        Assert.Equal(30, width3);
    }

    [Fact]
    public void Invalid_Technique_reports_IsValid_false()
    {
        var technique = new Technique(Shader.Invalid);
        Assert.False(technique.IsValid);
    }

    public static TheoryData<Type, string> Modified2DHotPaths => new()
    {
        { typeof(SpriteBatch), "RenderCaptured" },
        { typeof(SpriteBatch), "WriteCapturedUninterpolated" },
        { typeof(SpriteBatch), "WriteCapturedBlended" },
        { typeof(SpriteBatch), "Capture" },
        { typeof(SpriteBatch), "DisposeOther" },
        { typeof(TileMap), "Draw" },
        { typeof(TileMap), "PlaceImage" },
        { typeof(TileMap), "CanKeepImageBounds" },
        { typeof(TileMapGpu), "Draw" },
        { typeof(TileMapGpu), "Sync" },
        { typeof(DeferredRenderer2D), "BeforeResolve" },
        { typeof(DeferredRenderer2D), "BindLighting" },
        { typeof(DeferredRenderer2D), "BindLightList" },
        { typeof(DeferredRenderer2D), "CollectLights" },
        { typeof(PathTracedLighting2D), "Run" },
        { typeof(SpriteShadows), "Build" },
        { typeof(LightCells), "Build" },
        { typeof(LightTiles), "Build" },
        { typeof(CpuParticleSimulator2D), "Prepare" },
        { typeof(ComputeParticleSimulator2D), "Prepare" },
        { typeof(PrimitiveRenderer), "DrawBlended" },
        { typeof(PrimitiveRenderer), "BlendShapes" },
        { typeof(TextureAtlas), "FindCoalescedRunEnd" },
    };

    [Theory]
    [MemberData(nameof(Modified2DHotPaths))]
    public void Modified_2D_hot_paths_make_no_closure(Type type, string name)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL).Where(method => method.Name == name)];
        Assert.True(methods.Length > 0, $"{type.Name} has no method called {name}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"{type.Name}.{name} makes a closure ({closure}).");
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
