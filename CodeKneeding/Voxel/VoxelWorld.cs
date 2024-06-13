using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using CodeKneading.Rendering;
using Horizon.Engine;
using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal class VoxelWorld : GameObject
{
    public readonly WorldRenderer Renderer;
    public readonly SkyManager Sky;

    public const int LOADED_DISTANCE = 128;
    private const int LOD_PARTITION_DIST = 128;
    public const int HEIGHT = 8;

    public static readonly TileChunk[] Chunks = new TileChunk[LOADED_DISTANCE * LOADED_DISTANCE * HEIGHT];

    public VoxelWorld()
    {
        Renderer = AddComponent<WorldRenderer>(new(this));
        Sky = AddComponent<SkyManager>();
    }

    static void CalculateLODLevel()
    {
        Parallel.ForEach(Chunks, (chunk) =>
        {
            chunk.LOD = GetLod(in chunk);
        });
    }

    static int GetLod(ref readonly TileChunk chunk)
    {
        return Math.Clamp((int)Vector3.Distance(new Vector3(chunk.ChunkPosition.X * TileChunk.SIZE, chunk.ChunkPosition.Y * TileChunk.SIZE, chunk.ChunkPosition.Z * TileChunk.SIZE), Player.GamePlayer.Transform.Position) / LOD_PARTITION_DIST, 0, 4);
    }

    public override void Initialize()
    {
        base.Initialize();
        WorldChunkLoaderAsync();
    }

    private void WorldChunkLoaderAsync()
    {
        Span<TileChunk> chunkSpan = new(Chunks);

        Task<TileChunk>[] dataTasks = new Task<TileChunk>[Chunks.Length];
        for (int z = 0; z < LOADED_DISTANCE; z++)
        {
            for (int x = 0; x < LOADED_DISTANCE; x++)
            {
                for (int y = 0; y < HEIGHT; y++)
                {
                    int index = x + (y * LOADED_DISTANCE) + (z * LOADED_DISTANCE * HEIGHT);
                    chunkSpan[index] = (new TileChunk(new Vector3D<int>(x, y, z)));
                }
            }
        }
        CalculateLODLevel();


        Task.Run(() => Parallel.For(0, Chunks.Length, (index) => ChunkDataGenerator.GenerateTiles(Chunks[index])))
            .ContinueWith((_) => Task.Run(() => Parallel.For(0, Chunks.Length, (index) => ChunkMeshGenerator.GenerateMesh(Chunks[index]))));
    }

    int currentLod = -1;
    public override void UpdateState(float dt)
    {
        int key =
            Engine.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.Number0) ? 0 :
            Engine.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.Number1) ? 1 :
            Engine.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.Number2) ? 2 :
            Engine.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.Number3) ? 3 :
            Engine.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.Number4) ? 4 : -1;

        if (key != -1 && currentLod != key)
        {
            var chunkSpan = new ReadOnlySpan<TileChunk>(Chunks);
            currentLod = key;
            Renderer.BufferManager.ChunkletManager.FreeAll();

            for (int i = 0; i < Chunks.Length; i++)
            {
                chunkSpan[i].LOD = currentLod;
                ChunkMeshGenerator.GenerateMeshAsync(chunkSpan[i]);
            }

        }

        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Silk.NET.Input.Key.F))
        {
            currentLod = -1;
            //Array.Sort(Chunks, new ChunkDistanceComparer(Player.GamePlayer.Transform.Position));
            var chunkSpan = new ReadOnlySpan<TileChunk>(Chunks);
            CalculateLODLevel();
            Renderer.BufferManager.ChunkletManager.FreeAll();


            for (int i = 0; i < Chunks.Length; i++)
            {
                ChunkMeshGenerator.GenerateMeshAsync(chunkSpan[i]);
            }
        }

        base.UpdateState(dt);
    }

    public static Tile GetTile(in int x, in int y, in int z)
    {
        if (x < 0 || y < 0 || z < 0)
            return Tile.OOB;

        int chunkX = x / TileChunk.SIZE;
        int chunkZ = z / TileChunk.SIZE;
        int chunkY = y / TileChunk.SIZE;

        if (chunkX >= LOADED_DISTANCE || chunkZ >= LOADED_DISTANCE || y >= TileChunk.SIZE * HEIGHT || chunkY >= HEIGHT)
            return Tile.OOB;

        int localX = x % TileChunk.SIZE;
        int localY = y % TileChunk.SIZE;
        int localZ = z % TileChunk.SIZE;
        int index = (chunkZ * LOADED_DISTANCE * HEIGHT) + (chunkY * LOADED_DISTANCE) + chunkX;
        return Chunks[index][localX, localY, localZ];
    }
}