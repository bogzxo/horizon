namespace VoxelExplorer.Data
{
    internal class Chunk
    {
        public const int SIZE = 32;

        public IChunkDataProvider DataProvider { get; private set; }
        public int BufferSize { get; internal set; }
        public int BufferOffset { get; internal set; }

        public Chunk()
        {
            DataProvider = new BasicChunkDataProvider(SIZE, SIZE, SIZE);
            DataProvider.Populate((x, y, z) => new Voxel((byte)(/*Random.Shared.NextDouble() > 0.5 ? 1 : 0)*/1)));
        }
    }
}