using AutoVoxel.Data;
using AutoVoxel.Generator.Paralleliser;
using AutoVoxel.Rendering;

using Horizon.Engine;

namespace AutoVoxel.World;

public class GameWorld : GameObject
{
    public ChunkManager ChunkManager { get; }
    public ChunkRenderer ChunkRenderer { get; }

    public GameWorld()
    {
        ChunkManager = AddComponent<ChunkManager>();
        ChunkRenderer = AddComponent<ChunkRenderer>(new(ChunkManager));
    }

    public override void Initialize()
    {
        base.Initialize();
    }
}