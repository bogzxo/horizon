using System.Numerics;

using AutoVoxel.Data.Chunks;

using CodeKneading.Player;
using CodeKneading.Rendering;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;

using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal class SkyManager : IGameComponent
{
    public bool Enabled { get; set; }
    public string Name { get; set; }
    public Entity Parent { get; set; }

    public static Matrix4x4 SunProj { get; private set; }

    public static Matrix4x4 SunView;
    public Vector3 SunPosition { get; private set; }
    public Vector3 SunDirection { get => Vector3.Normalize(new Vector3(128, 0, 128) - SunPosition); }


    public SkyManager()
    {
        Name = "SkyManager";
        Enabled = true;
    }

    public void Initialize()
    {

    }

    public void Render(float dt, object? obj = null)
    {

    }

    public void UpdatePhysics(float dt)
    {

    }

    float timer = 0.0f;
    public void UpdateState(float dt)
    {
        timer += dt;
        SunProj = Matrix4x4.CreateOrthographic(250, 250, 100, 400.0f);
        SunPosition = new Vector3(MathF.Sin(timer / (60.0f * 20.0f)) * 256, 128, MathF.Cos(timer / (60.0f * 20.0f)) * 256);
        SunView = Matrix4x4.CreateLookAt(SunPosition, new Vector3(128, 0, 128), Vector3.UnitY);
    }
}

internal class VoxelWorld : GameObject
{
    internal static readonly ParallelQueueWorker DataGeneratorWorker = new();
    internal static readonly ParallelQueueWorker MeshGeneratorWorker = new();

    public readonly WorldRenderer Renderer;
    public readonly SkyManager Sky;

    public const int WIDTH = 8;
    public const int DEPTH = 8;

    public static readonly int CHUNK_COUNT = WIDTH * DEPTH;

    public VoxelWorld()
    {
        Chunks = new TileChunk[WIDTH * DEPTH];

        Renderer = AddComponent<WorldRenderer>(new(this));
        Sky = AddComponent<SkyManager>();

        DataGeneratorWorker.StartTask();
        MeshGeneratorWorker.StartTask();

        // idk why i do that
        Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < CHUNK_COUNT; i++)
            {
                Chunks[i] = new TileChunk(new Vector2D<int>(i % WIDTH, i / DEPTH));
                DataGeneratorWorker.Enqueue(ChunkDataGenerator.GenerateTilesAsync(Chunks[i]));
            }
        });
    }

    protected override void DisposeOther()
    {
        DataGeneratorWorker.Dispose();
        MeshGeneratorWorker.Dispose();
    }

    public Tile this[int x, int y, int z]
    {
        get
        {
            if (x < 0 || y < 0 || z < 0)
                return Tile.Empty;

            int chunkX = x / TileChunk.SIZE;
            int chunkY = z / TileChunk.SIZE;

            if (chunkX >= WIDTH || chunkY >= DEPTH || y >= TileChunk.SIZE)
                return Tile.Empty;

            int localX = x % TileChunk.SIZE;
            int localZ = z % TileChunk.SIZE;

            return Chunks[chunkX + (chunkY * WIDTH)].GetFloor(localX, y, localZ);
        }
    }

    public TileChunk[] Chunks { get; init; }
}