namespace VoxelExplorer.Data;

internal class VoxelWorld
{
    public const int WIDTH = 2;
    public const int DEPTH = 2;

    internal Chunk[] Chunks;

    public VoxelWorld()
    {
        Chunks = new Chunk[WIDTH * DEPTH];
        for (int i = 0; i < Chunks.Length; i++)
        {
            Chunks[i] = new Chunk();
        }
    }
}