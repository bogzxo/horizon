using CodeKneading.Rendering;

using Horizon.Core;

using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CodeKneading.Voxel;

internal static class ChunkMeshGenerator
{
    private static readonly Dictionary<VoxelFace, (int x, int y, int z)> faceOffsets = new() {
        { VoxelFace.Left, (-1, 0, 0) },
        { VoxelFace.Right, (1, 0, 0) },
        { VoxelFace.Top, (0, -1, 0) },
        { VoxelFace.Bottom, (0, 1, 0) },
        { VoxelFace.Front, (0, 0, 1) },
        { VoxelFace.Back, (0, 0, -1) },
    };

    private static readonly Dictionary<VoxelFace, VoxelFace> oppositeFaces = new(){
        { VoxelFace.Left, VoxelFace.Right },
        { VoxelFace.Right, VoxelFace.Left },
        { VoxelFace.Top, VoxelFace.Bottom},
        { VoxelFace.Bottom, VoxelFace.Top },
        { VoxelFace.Front, VoxelFace.Back},
        { VoxelFace.Back, VoxelFace.Front },
    };

    public static Task GenerateMeshAsync(TileChunk chunk)
    {
        return Task.Factory.StartNew(() =>
        {
            List<VoxelInstance> data = [];
            for (int z = 0; z < TileChunk.SIZE; z++)
            {
                for (int y = 0; y < TileChunk.SIZE; y++)
                {
                    for (int x = 0; x < TileChunk.SIZE; x++)
                    {
                        if (chunk.GroundTiles[x, y, z].Type == TileType.None) continue;

                        for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                        {
                            VoxelFace face = (VoxelFace)faceIndex;
                            (int xOffset, int yOffset, int zOffset) = faceOffsets[face];

                            Tile tile = chunk.GetFloor(x + xOffset, y + yOffset, z + zOffset);

                            if (tile.Type == TileType.None)
                                data.Add(VoxelInstance.Encode(tile, oppositeFaces[face], new(x, y, z)));
                        }
                    }
                }
            }
            
            // add ourselves to mesh upload queue
            WorldRenderer.MeshUploadQueue.Enqueue(new ChunkMeshData
            {
                Data = ListAdapter<VoxelInstance>.ToReadOnlyMemory(data),
                ChunkPos = chunk.ChunkPosition * TileChunk.SIZE,
            });
        });
    }
}
