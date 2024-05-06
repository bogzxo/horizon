using System.Runtime.InteropServices;

using Horizon.Core;

namespace VoxelExplorer.Data
{
    internal class Chunk(in int index, in int x, in int z)
    {
        public const int SIZE = 32;

        public IChunkDataProvider DataProvider { get; init; } = new BasicChunkDataProvider(SIZE, SIZE, SIZE);
        public uint BufferSize { get; internal set; }
        public uint BufferOffset { get; internal set; }

        /// <summary>
        /// Returns whether or not a chunk has had its voxel information generated.
        /// </summary>
        public bool IsReady { get; protected set; }

        public int Index { get; init; } = index;

        public int PosX { get; init; } = x;
        public int PosZ { get; init; } = z;

        public void GenerateData()
        {
            DataProvider.Populate(CalculateVoxel);
            VoxelWorldRenderer.VoxelMeshGenerator.EnqueueChunk(this);
        }

        private Voxel CalculateVoxel(int x, int y, int z)
        {
            float sample = Noise.CalcPixel3D(x + PosX * SIZE, y, z + PosZ * SIZE, 0.01f);

            bool state = (x % 2 == 0) || (z % 2 == 0);

            return new Voxel((byte)(state ? 1 : 0));
        }

        private enum CubeFace : byte
        {
            Bottom = 0,
            Top = 1,
            Front = 2,
            Back = 3,
            Right = 4,
            Left = 5,
        }

        private static readonly Dictionary<CubeFace, (int, int, int)> faceOpposingPairs = new() {
            { CubeFace.Back, (0, 0, -1) },
            { CubeFace.Front, (0, 0, 1) },
            { CubeFace.Left, (-1, 0, 0) },
            { CubeFace.Right, (1, 0, 0) },
            { CubeFace.Top, (0, -1, 0) },
            { CubeFace.Bottom, (0, 1, 0) },
        };


        public void GenerateMesh()
        {
            var instance_data_left = new List<VoxelInstanceData>();
            var instance_data_right = new List<VoxelInstanceData>();
            var instance_data_top = new List<VoxelInstanceData>();
            var instance_data_bottom = new List<VoxelInstanceData>();
            var instance_data_front = new List<VoxelInstanceData>();
            var instance_data_back = new List<VoxelInstanceData>();

            /* TODO: An idea for potential optimizations is to rethink how the data is iterated: generate a binary bitmap
             * for each block type and use use binary operation (invert, shift l or r, xor) to detect start and stop
             * of each type of tile, since we know all tiles are the same, they can be grouped into a single larger 
             * primitive, aka. greedy meshing. For now this will suffice.
             */

            List<VoxelInstanceData> faceArray(in CubeFace face)
            {
                return face switch
                {
                    CubeFace.Bottom => instance_data_bottom,
                    CubeFace.Top => instance_data_top,
                    CubeFace.Front => instance_data_front,
                    CubeFace.Back => instance_data_back,
                    CubeFace.Right => instance_data_right,
                    CubeFace.Left => instance_data_left,
                };
            }

            // TODO: flatten array
            for (int x = 0; x < SIZE; x++)
            {
                for (int y = 0; y < SIZE; y++)
                {
                    for (int z = 0; z < SIZE; z++)
                    {
                        // Don't attempt to mesh an empty tile
                        if (DataProvider[x, y, z].DataPack0 == 0) continue;


                        // Add voxel instances to their separate meshes if there's no tile present (DataPack0 == 0).
                        // I am grouping them like this so that i can use a compute shader to do backface culling
                        // No need to do bounds checking, its handles in the data provider
                        for (int i = 0; i < 6; i++)
                        {
                            var face = (CubeFace)i;

                            var (xOff, yOff, zOff) = faceOpposingPairs[face];

                            if (DataProvider[x + xOff, y + yOff, z + zOff].DataPack0 == 0)
                                faceArray(face).Add(VoxelInstanceData.Encode(x, y, z, i));
                        }
                    }
                }
            }

            VoxelWorldRenderer.MeshUploadQueue.Enqueue(new VoxelWorldRenderer.MeshUploadInfo
            {
                Index = Index,
                Array = ListAdapter<VoxelInstanceData>.ToMemory([.. instance_data_bottom, .. instance_data_top, .. instance_data_front, .. instance_data_back, .. instance_data_right, .. instance_data_left]), // Can't modify the list if its an expression
                LeftCount = (uint)instance_data_left.Count,
                RightCount = (uint)instance_data_right.Count,
                BackCount = (uint)instance_data_back.Count,
                BottomCount = (uint)instance_data_bottom.Count,
                FrontCount = (uint)instance_data_front.Count,
                TopCount = (uint)instance_data_top.Count
            });
        }

        public Task GenerateDataAsync() => Task.Factory.StartNew(GenerateData);

        public Task GenerateMeshAsync() => Task.Factory.StartNew(GenerateMesh);
    }
}