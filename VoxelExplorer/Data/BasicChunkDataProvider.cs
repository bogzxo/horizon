namespace VoxelExplorer.Data;

/// <summary>
/// Basic chunk storage for testing purposes, n^3 spacial complexity and a multidimensional array. TODO: improve
/// </summary>
internal class BasicChunkDataProvider : IChunkDataProvider
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }

    private readonly Voxel[,,] array;

    public Voxel this[in int x, in int y, in int z]
    {
        get
        {
            if (x >= Width || y >= Height || z >= Depth || x < 0 || y < 0 || z < 0)
                return Voxel.Empty;

            return array[x, y, z];
        }
        set
        {
            if (x > Width - 1 || y > Height - 1 || z > Depth - 1 || x < 0 || y < 0 || z < 0)
                return;
            array[x, y, z] = value;
        }
    }

    public BasicChunkDataProvider(in int width, in int height, in int depth)
    {
        Width = width;
        Height = height;
        Depth = depth;

        array = new Voxel[Width, Height, Depth];
    }

    public void Populate(Func<int, int, int, Voxel>? generator = null)
    {
        if (generator is null)
        {
            for (int z = 0; z < Depth; z++) // Corrected loop order
            {
                for (int y = 0; y < Height; y++) // Corrected loop order
                {
                    for (int x = 0; x < Width; x++)
                    {
                        array[x, y, z] = new Voxel(1);
                    }
                }
            }
            return;
        }

        for (int z = 0; z < Depth; z++) // Corrected loop order
        {
            for (int y = 0; y < Height; y++) // Corrected loop order
            {
                for (int x = 0; x < Width; x++)
                {
                    array[x, y, z] = generator(x, y, z); // Corrected indexing calculation
                }
            }
        }
    }
}
