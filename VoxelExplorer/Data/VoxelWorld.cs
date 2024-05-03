namespace VoxelExplorer.Data;

internal class VoxelWorld
{
    public const int WIDTH = 16;
    public const int DEPTH = 16;

    internal Chunk[] Chunks;

    private readonly VoxelWorldGenerator worldGenerator;

    public VoxelWorld(in VoxelWorldGenerator generator)
    {
        Chunks = new Chunk[WIDTH * DEPTH];
        worldGenerator = generator;

        for (int i = 0; i < Chunks.Length; i++)
        {
            Chunks[i] = new(i, i % Chunk.SIZE, i / Chunk.SIZE);
            worldGenerator.EnqueueChunk(Chunks[i]); //  TODO: make less hacky
        }
    }
}