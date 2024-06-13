using CodeKneading.Voxel;

namespace CodeKneading.Rendering
{
    internal static class ChunkDataGenerator
    {
        private static readonly float[,] heightMap = new float[TileChunk.SIZE * VoxelWorld.LOADED_DISTANCE, TileChunk.SIZE * VoxelWorld.LOADED_DISTANCE];
        private const float floorHeight = 8.0f; // Adjust this to set the height of the floor

        static ChunkDataGenerator()
        {
            for (int x = 0; x < VoxelWorld.LOADED_DISTANCE * TileChunk.SIZE; x++)
            {
                for (int z = 0; z < VoxelWorld.LOADED_DISTANCE * TileChunk.SIZE; z++)
                {
                    heightMap[x, z] = (float)Math.Pow((
                        (Perlin.OctavePerlin(x * 0.004, 0, z * 0.004, 5, 0.2)) // local tile height
                        ), 2) * TileChunk.SIZE * VoxelWorld.HEIGHT;
                }
            }
        }

        public static Task GenerateTilesAsync(TileChunk chunk) => Task.Run(() => GenerateTiles(chunk));

        public static void GenerateTiles(TileChunk chunk)
        {
            for (int z = 0; z < TileChunk.SIZE; z++)
            {
                for (int x = 0; x < TileChunk.SIZE; x++)
                {
                    for (int y = 0; y < TileChunk.SIZE; y++)
                    {
                        chunk[x, y, z] = new Tile { Type = GetType(x + chunk.ChunkPosition.X * TileChunk.SIZE, chunk.ChunkPosition.Y * TileChunk.SIZE + y, chunk.ChunkPosition.Z * TileChunk.SIZE + z) };
                    }
                }
            }
        }

        private static TileType GetType(int x, int y, int z)
        {
            int localY = y;

            if (localY > heightMap[x, z])
            {
                return TileType.None; // Otherwise, set as empty space
            }

            if (localY <= floorHeight) // Check if below or at floor height
            {
                localY = (int)(Perlin.perlin(x * 0.04, 0, z * 0.04) * floorHeight);
                if (y < localY)
                    return TileType.Rock;
            }

            //if (Perlin.perlin(x * 0.002, localY * 0.002, z * 0.002) > 0.5)
            {
                return Random.Shared.NextSingle() > 0.3f ? TileType.Ground : TileType.Rock;
            }
            return TileType.None;
        }
    }
}
