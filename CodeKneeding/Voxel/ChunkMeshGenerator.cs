using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using CodeKneading.Rendering;

using Horizon.Core;

using Silk.NET.Core;
using Silk.NET.Maths;

using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CodeKneading.Voxel;

internal static class ChunkMeshGenerator
{
    private static readonly BlockingCollection<ChunkMeshData> failedQueue = [];

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint[] GenVoxelFaceVertices(in VoxelFace face, Vector3D<int> localPos, in int lod)
    {
        return face switch
        {
            VoxelFace.Top => [
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 0)), new Vector3D<int>(0, lod, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 0)), new Vector3D<int>(lod, lod, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 1)), new Vector3D<int>(lod, lod, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 1)), new Vector3D<int>(0, lod, lod) + localPos),
            ],
            VoxelFace.Bottom => [
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 0)), new Vector3D<int>(0, 0, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 0)), new Vector3D<int>(lod, 0, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 1)), new Vector3D<int>(lod, 0, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 1)), new Vector3D<int>(0, 0, lod) + localPos),
            ],
            VoxelFace.Front => [
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 0)), new Vector3D<int>(0, 0, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 0)), new Vector3D<int>(lod, 0, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 1)), new Vector3D<int>(lod, lod, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 1)), new Vector3D<int>(0, lod, 0) + localPos),
            ],
            VoxelFace.Back => [
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 0)), new Vector3D<int>(0, lod, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 0)), new Vector3D<int>(lod, lod, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 1)), new Vector3D<int>(lod, 0, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 1)), new Vector3D<int>(0, 0, lod) + localPos),
            ],
            VoxelFace.Left => [
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 0)), new Vector3D<int>(lod, 0, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 0)), new Vector3D<int>(lod, lod, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 1)), new Vector3D<int>(lod, lod, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 1)), new Vector3D<int>(lod, 0, 0) + localPos),
            ],
            VoxelFace.Right => [
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 0)), new Vector3D<int>(0, 0, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 0)), new Vector3D<int>(0, lod, 0) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (1, 1)), new Vector3D<int>(0, lod, lod) + localPos),
                VoxelVertex.EncodeVertexPosition(VoxelVertex.EncodeTexCoords(new (0, 1)), new Vector3D<int>(0, 0, lod) + localPos),
            ],
            _ => throw new NotImplementedException(),
        };
    }


    /* uint: 15663KB
     * ushort: 7831KB
     * 49.996% less mem */
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort[] GenIndices(in ushort offset)
        => [(ushort)(0 + offset), (ushort)(1 + offset), (ushort)(2 + offset), (ushort)(0 + offset), (ushort)(2 + offset), (ushort)(3 + offset)];

    static void addData(ref (List<VoxelVertex> vertices, List<ushort> indices, ushort indexOffset) data, Tile tile, in VoxelFace face, Vector3D<int> localPos, int lod, int cLod)
    {
        data.vertices.AddRange(GenVoxelFaceVertices(face, localPos, lod).Select((packedData) =>
        {
            // as the voxel type gains data and features, we can chain the construction here.
            return new VoxelVertex(VoxelVertex.EncodeLod(VoxelVertex.EncodeVertexID(packedData, tile.Type), cLod));
        }));

        data.indices.AddRange(GenIndices(data.indexOffset));

        data.indexOffset += 4;
    }

    public static Task GenerateMeshAsync(TileChunk chunk) => Task.Run(() => GenerateMesh(chunk));

    public static void GenerateMesh(TileChunk chunk)
    {
        int originalCount = failedQueue.Count;
        for (int i = 0; i < originalCount; i++)
        {
            if (failedQueue.TryTake(out ChunkMeshData _data))
            {
                if (!WorldBufferManager.MeshUploadQueue.TryAdd(_data))
                {
                    failedQueue.Add(_data);
                }
            }
        }

        if (chunk.IsEmpty) return;

        (List<VoxelVertex> vertices, List<ushort> indices, ushort indexOffset)[] data = [([], [], 0), ([], [], 0), ([], [], 0), ([], [], 0), ([], [], 0), ([], [], 0)];

        int lod = (int)Math.Pow(2, chunk.LOD);

        for (int z = 0; z < TileChunk.SIZE; z += lod)
        {
            for (int y = 0; y < TileChunk.SIZE; y += lod)
            {
                for (int x = 0; x < TileChunk.SIZE; x += lod)
                {
                    if (chunk[x, y, z].Type == TileType.None) continue;

                    // check visibility for each face
                    for (int faceIndex = 0; faceIndex < 6; faceIndex++)
                    {
                        VoxelFace face = (VoxelFace)faceIndex;
                        (int xOffset, int yOffset, int zOffset) = faceOffsets[face];

                        int lodX = x + (xOffset * lod) + (chunk.ChunkPosition.X * TileChunk.SIZE);
                        int lodY = y + (yOffset * lod) + (chunk.ChunkPosition.Y * TileChunk.SIZE);
                        int lodZ = z + (zOffset * lod) + (chunk.ChunkPosition.Z * TileChunk.SIZE);

                        Tile tile = VoxelWorld.GetTile(lodX, lodY, lodZ);

                        if (tile.Type != TileType.None) continue;

                        addData(ref data[faceIndex], chunk[x, y, z], oppositeFaces[face], new Vector3D<int>(x, y, z), lod, chunk.LOD);
                    }
                }
            }
        }

        // add ourselves to mesh upload queue
        ChunkMeshData meshData = new ChunkMeshData
        {
            Chunk = chunk,
            VerticesMemory = ListAdapter<VoxelVertex>.ToReadOnlyMemory([
                .. data[0].vertices,
                    .. data[1].vertices,
                    .. data[2].vertices,
                    .. data[3].vertices,
                    .. data[4].vertices,
                    .. data[5].vertices,
                ]),
            ElementsMemory = ListAdapter<ushort>.ToReadOnlyMemory([
                .. data[0].indices,
                    .. data[1].indices,
                    .. data[2].indices,
                    .. data[3].indices,
                    .. data[4].indices,
                    .. data[5].indices,
                ]),
            ElementCounts = [
                (uint)data[0].indices.Count,
                    (uint)data[1].indices.Count,
                    (uint)data[2].indices.Count,
                    (uint)data[3].indices.Count,
                    (uint)data[4].indices.Count,
                    (uint)data[5].indices.Count,
                    ],
            VertexCounts = [
                data[0].vertices.Count,
                    data[1].vertices.Count,
                    data[2].vertices.Count,
                    data[3].vertices.Count,
                    data[4].vertices.Count,
                    data[5].vertices.Count,
                    ],
            ChunkData = new()
            {
                xPos = chunk.ChunkPosition.X * TileChunk.SIZE,
                yPos = chunk.ChunkPosition.Y * TileChunk.SIZE,
                zPos = chunk.ChunkPosition.Z * TileChunk.SIZE
            },
        };


        if (!WorldBufferManager.MeshUploadQueue.TryAdd(meshData))
        {
            failedQueue.Add(meshData);
        }
    }
}
