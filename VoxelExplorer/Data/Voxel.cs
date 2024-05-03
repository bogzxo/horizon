namespace VoxelExplorer.Data
{
    public struct Voxel(in byte id)
    {
        public int DataPack0 { get; init; } = id;

        /*
         *  The CPU side data is encoded in a single UINT.
         * ┌────────────────────────────────────────────┐
         * │                                    (0 - 4) │
         * │                                    ID      │
         * │                                     │      │
         * │                                  ┌──▼─┐    │
         * │     0000 0000 0000 0000 0000 0000│0000│    │
         * │                                  └────┘    │
         * │                                            │
         * │                                            │
         * │                                            │
         * └────────────────────────────────────────────┘
         */

        public static Voxel Empty { get; } = new Voxel(0);
        public static Voxel OOB { get; } = new Voxel { DataPack0 = -1 };
    }
}