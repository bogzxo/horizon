using System.Numerics;

using AutoVoxel.Generator;
using AutoVoxel.Rendering;
using AutoVoxel.World;

using Horizon.Core;
using Horizon.Core.Primitives;

namespace AutoVoxel.Data.Chunks;

public class Chunk
{
    public const int SLICES = 2;
    public const int WIDTH = Slice.SIZE;
    public const int HEIGHT = Slice.SIZE * SLICES;
    public const int DEPTH = Slice.SIZE;

    public Slice[] Slices { get; }
    public int Index { get; }
    public Vector2 Position { get; }

    public Tile this[int x, int y, int z]
    {
        get => y >= HEIGHT ? Tile.OOB : y < 0 ? Tile.OOB : Slices[y / Slice.SIZE][x, y % Slice.SIZE, z];
        set
        {
            if (y >= HEIGHT || y < 0) return;

            Slices[y / Slice.SIZE][x, y % Slice.SIZE, z] = value;
        }
    }

    public Chunk(int index, in Vector2 position)
    {
        Index = index;
        Position = position;
        Slices = new Slice[SLICES];
        for (int i = 0; i < SLICES; i++)
            Slices[i] = new();
    }

    public void GenerateData()
    {
        for (int x = 0; x < WIDTH; x++)
        {
            for (int z = 0; z < DEPTH; z++)
            {
                int height = (int)(ChunkManager.Heightmap[(int)(x + Position.X * (WIDTH - 1)), (int)(z + Position.Y * (DEPTH - 1))] * (HEIGHT - 5));

                for (int y = height; y > 0; y--)
                {
                    if (Perlin.OctavePerlin((x + Position.X * (WIDTH - 1)) * 0.05, y * 0.05, (z + Position.Y * (DEPTH - 1)) * 0.05, 2, 0.5) > 0.7)
                    {
                        int localY = height - y;

                        if (y == height && Random.Shared.NextSingle() > 0.8f)
                            this[x, height + 1, z] = new Tile { ID = TileID.Grass };

                        this[x, y, z] = new Tile { ID = localY < 6 ? TileID.Dirt : TileID.Stone };
                    }
                }
            }
        }

        ChunkManager.MeshGeneratorWorker.Enqueue(GenerateMeshAsync());
    }

    public Task GenerateDataAsync() => Task.Factory.StartNew(GenerateData);
    public Task GenerateMeshAsync() => Task.Factory.StartNew(GenerateMesh);

    public void GenerateMesh()
    {
        List<ChunkVertex> verts = [];
        List<uint> slices = [];

        for (int sliceIndex = 0; sliceIndex < SLICES; sliceIndex++)
        {
            uint vertexCounter = 0;
            for (int y = sliceIndex * Slice.SIZE; y < Slice.SIZE + sliceIndex * Slice.SIZE; y++)
            {
                for (int x = 0; x < WIDTH; x++)
                {
                    for (int z = 0; z < DEPTH; z++)
                    {
                        var tile = this[x, y, z];

                        // skip rendering if the current voxel is empty
                        if ((int)tile.ID < 2)
                            continue;

                        // hack in grass
                        if (tile.ID == TileID.Grass)
                        {
                            //folTes.AddCross(new Tile { ID = TileID.Grass }, (int)x, (int)yRaw, (int)z);
                            continue;
                        }

                        // check each face of the voxel for visibility
                        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                        {
                            // calculate the position of the neighboring voxel
                            Vector3 neighbourPosition = GetNeighborPosition(
                                new Vector3(x, y, z),
                                (CubeFace)faceIndex
                            );

                            var neighborTile = ChunkManager.Instance[
                                (int)neighbourPosition.X,
                                (int)neighbourPosition.Y,
                                (int)neighbourPosition.Z
                            ];

                            // check if the neighboring voxel is empty or occludes the current voxel
                            if (neighborTile.ID == TileID.Air || neighborTile.ID == TileID.Grass)
                            {
                                // generate the face if the neighboring voxel is empty
                                verts.Add(ChunkVertex.Encode((int)neighbourPosition.X, (int)neighbourPosition.Y, (int)neighbourPosition.Z, (CubeFace)faceIndex, tile.ID));
                                vertexCounter++; // Count vertex offsets
                            }
                        }
                    }
                }
            }
            slices.Add(vertexCounter);
        }

        ChunkBufferManager.MeshUploadQueue.Enqueue(new()
        {
            Data = ListAdapter<ChunkVertex>.ToReadOnlyMemory(verts),
            Index = Index,
            OffsetX = (int)(Position.X * WIDTH),
            OffsetZ = (int)(Position.Y * DEPTH),
            SliceOffsets = [.. slices], // We can afford a copy for 4 elements
        });
    }

    private CubeFace GetOpposingFace(CubeFace face) => face switch
    {
        CubeFace.Left => CubeFace.Right,
        CubeFace.Right => CubeFace.Left,

        CubeFace.Front => CubeFace.Back,
        CubeFace.Back => CubeFace.Front,

        CubeFace.Top => CubeFace.Top,
        CubeFace.Bottom => CubeFace.Bottom
    };

    private Vector3 GetNeighborPosition(Vector3 position, CubeFace face)
    {
        return face switch
        {
            CubeFace.Front => new Vector3(position.X, position.Y, position.Z + 1),
            CubeFace.Back => new Vector3(position.X, position.Y, position.Z - 1),

            CubeFace.Left => new Vector3(position.X - 1, position.Y, position.Z),
            CubeFace.Right => new Vector3(position.X + 1, position.Y, position.Z),

            CubeFace.Top => new Vector3(position.X, position.Y - 1, position.Z),
            CubeFace.Bottom => new Vector3(position.X, position.Y + 1, position.Z),
            _ => position,
        } + new Vector3(Position.X * WIDTH, 0, Position.Y * DEPTH);
    }
}