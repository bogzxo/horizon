using System.Diagnostics;

using AutoVoxel.Data;
using AutoVoxel.Data.Chunks;

namespace AutoVoxel.Generator;

public class HeightmapGenerator
{
    private float[] heightmap;
    private readonly int sizeX, sizeY;
    private IHeightmapGeneratorPass[][] passes;

    public HeightmapGenerator(in ChunkManager manager)
    {
        heightmap = new float[Chunk.WIDTH * manager.Width * Chunk.DEPTH * manager.Height];
        Array.Fill(heightmap, 0.8f);
        sizeX = Chunk.WIDTH * manager.Width;
        sizeY = Chunk.DEPTH * manager.Height;

        passes = [
            [
                new HeightmapSurfacePass(1.5f),
                new RandomSurfacePass(0.01f, 0.3f),
                new SmoothSurfacePass(1.0f)
            ]
        ];
    }

    public void Generate()
    {
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < passes.Length; i++)
        {
            int targetSizeX = sizeX / (i + 1);
            int targetSizeY = sizeY / (i + 1);
            float[] target = new float[targetSizeX * targetSizeY];

            var currentPasses = passes[i];

            for (int x = 0; x < sizeX / targetSizeX; x++)
            {
                int xOffset = x * targetSizeX;
                for (int y = 0; y < sizeY / targetSizeY; y++)
                {
                    int yOffset = y * targetSizeY;

                    // Calculate the starting index for copying from the heightmap to the target array
                    int sourceStartIndex = xOffset + y * sizeX * targetSizeY;

                    // Copy the subarray from the heightmap to the target array
                    Buffer.BlockCopy(heightmap, sourceStartIndex * sizeof(float), target, 0, target.Length * sizeof(float));

                    // Execute the terrain generation pass on the target array
                    foreach (var pass in currentPasses)
                    {
                        pass.Execute(ref target, (i + 1), new Silk.NET.Maths.Vector2D<int>(targetSizeX, targetSizeY), new Silk.NET.Maths.Vector2D<int>(xOffset, yOffset));
                    }

                    // Copy the modified subarray from the target array back to the heightmap
                    Buffer.BlockCopy(target, 0, heightmap, sourceStartIndex * sizeof(float), target.Length * sizeof(float));
                }
            }
        }
        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Info, $"[WorldHeightmapGenerator] Done! Took {sw.ElapsedMilliseconds / 100.0f}s.");
    }

    public float this[int x, int z]
    {
        get => heightmap[x % sizeX + z * sizeY];
    }
}