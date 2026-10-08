using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// The lights of a frame sorted into the tiles of the screen by a compute pass (shaders/lighting/light_tiles.slang),
/// so every pixel of the deferred pass, and every ray of the path tracer, only looks at the lights that reach its
/// tile. A tile is <see cref="TILE_SIZE"/> pixels square and holds how many lights reach into it and then which.
/// </summary>
internal sealed class LightTiles : IDisposable
{
    /// <summary>Must match TILE_SIZE and TILE_WORDS in shaders/lighting/direct.slang.</summary>
    public const int TILE_SIZE = 32;
    public const int TILE_WORDS = 16;

    /// <summary>The binding the tiles are read at, which is what direct.slang says, and where the pass writes them.</summary>
    public const uint TILES_BINDING = 3;
    private const uint OUTPUT_BINDING = 4;

    private static Technique? shader;

    private GpuBuffer? buffer;
    private int capacityTiles;

    /// <summary>The tiles as the shaders read them, null before the first frame.</summary>
    public GpuBuffer? Buffer => buffer;

    /// <summary>How many tiles across and up.</summary>
    public Vector2 Counts { get; private set; }

    /// <summary>The size of the screen the tiles are over, in pixels.</summary>
    public Vector2 Screen { get; private set; }

    /// <summary>
    /// Sorts the lights of the frame into the tiles of a screen of a size, seen through a camera. Render thread, after
    /// the renderer has uploaded its lights and before anything reads them.
    /// </summary>
    public void Build(DeferredRenderer2D renderer, Camera camera, Vector2 screen)
    {
        if (!EnsureShader()) return;

        int tilesX = Math.Max(1, (int)MathF.Ceiling(screen.X / TILE_SIZE));
        int tilesY = Math.Max(1, (int)MathF.Ceiling(screen.Y / TILE_SIZE));
        int tiles = tilesX * tilesY;

        if (buffer is null || capacityTiles < tiles)
        {
            buffer?.Dispose();
            capacityTiles = Math.Max(tiles, capacityTiles * 2);
            buffer = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Dynamic, (nuint)(capacityTiles * TILE_WORDS * sizeof(uint))));
        }

        Counts = new Vector2(tilesX, tilesY);
        Screen = screen;

        var device = GraphicsDevice.Current;
        CameraBlock.Use(camera);

        shader!.Bind();
        renderer.BindLighting(shader);
        device.BindStorageBuffer(OUTPUT_BINDING, buffer);
        device.Dispatch((uint)((tilesX + 7) / 8), (uint)((tilesY + 7) / 8));
        device.Barrier(BarrierTargets.ShaderStorage);
        shader.Unbind();
        device.BindStorageBuffer(OUTPUT_BINDING, null);
    }

    private static bool EnsureShader()
    {
        if (shader is not null) return true;

        if (!GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet("light_tiles", ShaderDescription.FromPath("shaders/lighting", "light_tiles"), out var result))
            return false;

        shader = new Technique(result.Asset);
        return true;
    }

    public void Dispose()
    {
        buffer?.Dispose();
        buffer = null;
    }
}
