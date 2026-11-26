using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// The shadows sprites cast, as sharp as their pixels. Every sprite that blocks light (<see cref="Spriting.Sprite.CastsShadows"/>)
/// leaves where it was drawn in the material attachment of the G-buffer, and this turns that into a signed distance
/// field of the picture every frame on the GPU, as a jump flood (shaders/lighting/sprite_seed.slang, sprite_flood.slang
/// and sprite_field.slang). The flood runs on a grid <see cref="Scale"/> texels coarse, a dozen cheap passes that
/// get every cell the nearest edge within a couple of hundred texels, and the last pass reads the picture itself so
/// the field is exact at the sprites' edges. The lights march it alongside the distance field of the occlusion map,
/// see direct.slang, so a sprite throws a shadow and bounces light exactly where its texture has ink.
/// A second distance in the same field is to everything that blocks or is a lamp (the blue of the material, see
/// gbuffer.slang), which is what the path traced lighting stops its rays at, so whatever is drawn as one (a lamp,
/// sparks, a glowing sign) lights what is around it with its own shape and colour. Render thread.
/// </summary>
internal sealed class SpriteShadows : IDisposable
{
    /// <summary>How many picture texels across a cell of the flood's grid is. 2 is plenty, the last pass is exact anyway.</summary>
    public int Scale { get; set; } = 2;

    /// <summary>
    /// How far (in cells) the flood looks for a seed, which is its first step. Further than this from any sprite
    /// the field says this far, which is a distance it is safe to march by. 128 cells is a quarter of a 1080p
    /// picture at a scale of 2, eight passes.
    /// </summary>
    public int Reach { get; set; } = 128;

    /// <summary>std140 Params block for sprite_seed.slang (24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SeedParams
    {
        public Vector2 Size;
        public Vector2 Cells;
        public int Scale;
        public int SpritesBlock;
    }

    /// <summary>std140 Params block for sprite_flood.slang and sprite_flood_final.slang (16 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FloodParams
    {
        public Vector2 Cells;
        public int Step;
        public int Scale;
    }

    /// <summary>std140 Params block for sprite_field.slang (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FieldParams
    {
        public Vector2 Size;
        public Vector2 Cells;
        public int Scale;
        public float Far;
        public float PixelWorld;
        public int SpritesBlock;
    }

    private static readonly TextureDefinition Fields = new(PixelFormat.Rg16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);
    private static readonly TextureDefinition Seeds = new(PixelFormat.Rg32Uint, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    private static Technique? seed, flood, floodFinal, field;

    private Texture? seedsA, seedsB, result;
    private uint width, height, cells, rows;
    private int builtScale;

    /// <summary>The field as it was last built, null before the first time.</summary>
    public Texture? Field => result;

    /// <summary>
    /// Builds the field of a picture. Render thread, with the G-buffer drawn and nothing rendering.
    /// </summary>
    /// <param name="material">The material attachment of the G-buffer, over 0.5 in the green where something blocks light.</param>
    /// <param name="surface">The surface attachment of the G-buffer, the blue of which is how emissive a pixel is.</param>
    /// <param name="pixelWorld">How big a pixel of the picture is in world units.</param>
    /// <param name="spritesBlockRays">Whether the second distance counts what blocks as well as what glows, see <see cref="PathTracedLighting2D.SpritesBlock"/>.</param>
    /// <returns>The field, null if it couldn't be made.</returns>
    public Texture? Build(Texture material, Texture surface, uint pictureWidth, uint pictureHeight, float pixelWorld, bool spritesBlockRays = true)
    {
        int scale = Math.Clamp(Scale, 1, 8);
        if (!EnsureShaders() || !Fit(pictureWidth, pictureHeight, scale)) return null;

        var device = GraphicsDevice.Current;
        var size = new Vector2(width, height);
        var grid = new Vector2(cells, rows);
        uint groupsX = (cells + 7) / 8, groupsY = (rows + 7) / 8;

        // The seeds, an edge texel of what blocks and of what glows for every cell that has one
        using (device.BeginGpuScope("shadow seeds"))
        {
            seed!.Bind();
            var seedParams = new SeedParams
            {
                Size = size,
                Cells = grid,
                Scale = scale,
                SpritesBlock = spritesBlockRays ? 1 : 0
            };
            seed.SetParamsBlock(in seedParams);
            material.Bind(0);
            device.BindStorageImage(0, seedsA);
            device.Dispatch(groupsX, groupsY);
            device.Barrier(BarrierTargets.ShaderImages);
        }

        // The flood, from the longest step down to one, back and forth between the two seed textures.
        // Steps 4, 2 and 1 are fused into a single shared-memory pass when maxReach >= 4.
        int maxReach = Math.Min(Math.Max(1, Reach), (int)Math.Max(cells, rows));
        int step = 1;
        while (step * 2 <= maxReach) step *= 2;
        float far = step * 2.0f * scale;

        Texture from = seedsA!, into = seedsB!;
        using (device.BeginGpuScope("shadow flood"))
        {
            if (step >= 4 && floodFinal is { IsValid: true })
            {
                if (step >= 8)
                {
                    flood!.Bind();
                    for (; step >= 8; step /= 2)
                    {
                        var floodParams = new FloodParams { Cells = grid, Step = step, Scale = scale };
                        flood.SetParamsBlock(in floodParams);
                        device.BindStorageImage(0, from);
                        device.BindStorageImage(1, into);
                        device.Dispatch(groupsX, groupsY);
                        device.Barrier(BarrierTargets.ShaderImages);
                        (from, into) = (into, from);
                    }
                }

                floodFinal.Bind();
                var finalParams = new FloodParams { Cells = grid, Step = 4, Scale = scale };
                floodFinal.SetParamsBlock(in finalParams);
                device.BindStorageImage(0, from);
                device.BindStorageImage(1, into);
                device.Dispatch((cells + 15) / 16, (rows + 15) / 16);
                device.Barrier(BarrierTargets.ShaderImages);
                (from, into) = (into, from);
            }
            else
            {
                flood!.Bind();
                for (; step >= 1; step /= 2)
                {
                    var floodParams = new FloodParams { Cells = grid, Step = step, Scale = scale };
                    flood.SetParamsBlock(in floodParams);
                    device.BindStorageImage(0, from);
                    device.BindStorageImage(1, into);
                    device.Dispatch(groupsX, groupsY);
                    device.Barrier(BarrierTargets.ShaderImages);
                    (from, into) = (into, from);
                }
            }
        }

        // And the distances, exact, for every texel of the picture
        using (device.BeginGpuScope("shadow distances"))
        {
            field!.Bind();
            var fieldParams = new FieldParams
            {
                Size = size,
                Cells = grid,
                Scale = scale,
                Far = far,
                PixelWorld = pixelWorld,
                SpritesBlock = spritesBlockRays ? 1 : 0
            };
            field.SetParamsBlock(in fieldParams);
            material.Bind(0);
            device.BindStorageImage(0, from);
            device.BindStorageImage(1, result);
            device.Dispatch((width + 7) / 8, (height + 7) / 8);
            device.Barrier(BarrierTargets.ShaderImages);
            field.Unbind();
        }

        device.BindStorageImage(0, null);
        device.BindStorageImage(1, null);
        return result;
    }

    private bool Fit(uint pictureWidth, uint pictureHeight, int scale)
    {
        if (result is not null && width == pictureWidth && height == pictureHeight && builtScale == scale) return true;

        Release();
        width = pictureWidth;
        height = pictureHeight;
        builtScale = scale;
        cells = (width + (uint)scale - 1) / (uint)scale;
        rows = (height + (uint)scale - 1) / (uint)scale;

        var textures = GameEngine.Instance.ObjectManager.Textures;
        if (!textures.TryCreate(new TextureDescription { Width = cells, Height = rows, Definition = Seeds }, out var a))
            return false;
        seedsA = a.Asset;
        seedsA.Name = "sprite shadow seeds a";

        if (!textures.TryCreate(new TextureDescription { Width = cells, Height = rows, Definition = Seeds }, out var b))
        {
            Release();
            return false;
        }
        seedsB = b.Asset;
        seedsB.Name = "sprite shadow seeds b";

        if (!textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = Fields }, out var f))
        {
            Release();
            return false;
        }
        result = f.Asset;
        result.Name = "sprite shadow field";
        return true;
    }

    private static bool EnsureShaders()
    {
        if (seed is { IsValid: true } && flood is { IsValid: true } && floodFinal is { IsValid: true } && field is { IsValid: true }) return true;

        var shaders = GameEngine.Instance.ObjectManager.Shaders;
        if (!shaders.TryCreateOrGet("sprite_seed", ShaderDescription.FromPath("shaders/lighting", "sprite_seed"), out var s)) return false;
        if (!shaders.TryCreateOrGet("sprite_flood", ShaderDescription.FromPath("shaders/lighting", "sprite_flood"), out var j)) return false;
        if (!shaders.TryCreateOrGet("sprite_flood_final", ShaderDescription.FromPath("shaders/lighting", "sprite_flood_final"), out var jf)) return false;
        if (!shaders.TryCreateOrGet("sprite_field", ShaderDescription.FromPath("shaders/lighting", "sprite_field"), out var f)) return false;

        seed = new Technique(s.Asset);
        flood = new Technique(j.Asset);
        floodFinal = new Technique(jf.Asset);
        field = new Technique(f.Asset);
        GraphicsDevice.Current.WarmComputePipelines([s.Asset, j.Asset, jf.Asset, f.Asset]);
        return true;
    }

    private void Release()
    {
        seedsA?.Dispose();
        seedsB?.Dispose();
        result?.Dispose();
        seedsA = seedsB = result = null;
    }

    public void Dispose() => Release();
}
