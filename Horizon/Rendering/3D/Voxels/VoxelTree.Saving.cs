using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Horizon.Rendering.Voxels;

public sealed partial class VoxelTree
{
    private const uint MAGIC = 0x52545856; // "VXTR"
    private const int FORMAT = 1;

    /// <summary>
    /// Throws out every node and voxel nobody points at any more (what changes leave behind), and puts everything in
    /// the order a ray would go through it. Copies the lot, so not every frame, on a save or when
    /// <see cref="Garbage"/> gets big.
    /// </summary>
    public void Compact()
    {
        lock (SyncRoot)
        {
            long liveNodes = nodeCount - Math.Min(Garbage, nodeCount - 1L);
            var newNodes = new uint[Capacity(Math.Max(16L, liveNodes * 9 / 8 + 1024) * NODE_UINTS)];
            var newVoxels = new byte[Capacity(Math.Max(64L, (long)voxelCount * 9 / 8 + 1024))];
            int newNodeCount = 1, newVoxelCount = 0;

            NodeRecord root = Copy(ReadNode(0));
            root.Write(newNodes.AsSpan(0, NODE_UINTS));

            nodes = newNodes;
            voxels = newVoxels;
            nodeCount = newNodeCount;
            voxelCount = newVoxelCount;
            Garbage = 0;
            changedNodes.Clear();
            changedVoxels.Clear();
            rebuilt = true;
            Version++;

            NodeRecord Copy(NodeRecord node)
            {
                if (node.IsEmpty || node.IsUniform) return node;

                int count = BitOperations.PopCount(node.Mask);
                if (node.IsLeaf)
                {
                    if (newVoxelCount + count > newVoxels.Length) Array.Resize(ref newVoxels, Capacity((long)newVoxels.Length * 3 / 2 + count));
                    voxels.AsSpan((int)node.Child, count).CopyTo(newVoxels.AsSpan(newVoxelCount));
                    int voxelStart = newVoxelCount;
                    newVoxelCount += count;
                    return node with { Child = (uint)voxelStart };
                }

                // The children's places first, so they sit together, then what is under each of them
                int start = newNodeCount;
                newNodeCount += count;
                if ((long)newNodeCount * NODE_UINTS > newNodes.Length) Array.Resize(ref newNodes, Capacity((long)newNodes.Length * 3 / 2 + count * NODE_UINTS));

                for (int i = 0; i < count; i++)
                {
                    NodeRecord child = Copy(ReadNode((int)node.Child + i));
                    child.Write(newNodes.AsSpan((start + i) * NODE_UINTS));
                }

                return node with { Child = (uint)start };
            }
        }
    }

    /// <summary>
    /// Writes the world out, squeezed. Compacted first unless asked not to, which a world being played in is (a compacted
    /// world is new arrays, and the renderer would have to send all of it up again, see <see cref="VoxelRenderer"/>).
    /// Compact it after reading it back instead.
    /// </summary>
    public void Save(Stream stream, bool compact = true)
    {
        if (compact) Compact();

        lock (SyncRoot)
        {
            using var squeezed = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new BinaryWriter(squeezed);
            writer.Write(MAGIC);
            writer.Write(FORMAT);
            writer.Write(SizeX);
            writer.Write(SizeY);
            writer.Write(SizeZ);
            writer.Write(nodeCount);
            writer.Write(voxelCount);
            writer.Write(MemoryMarshal.AsBytes(nodes.AsSpan(0, nodeCount * NODE_UINTS)));
            writer.Write(voxels.AsSpan(0, voxelCount));
        }
    }

    /// <summary>Reads a world <see cref="Save"/> wrote, made of the palette it was made with.</summary>
    public static VoxelTree Load(Stream stream, VoxelPalette palette)
    {
        using var squeezed = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new BinaryReader(squeezed);
        if (reader.ReadUInt32() != MAGIC) throw new InvalidDataException("That's not a voxel world.");
        int format = reader.ReadInt32();
        if (format != FORMAT) throw new InvalidDataException($"A voxel world of format {format}, this reads {FORMAT}.");

        int sizeX = reader.ReadInt32(), sizeY = reader.ReadInt32(), sizeZ = reader.ReadInt32();
        int nodeCount = reader.ReadInt32(), voxelCount = reader.ReadInt32();

        var tree = new VoxelTree(sizeX, sizeY, sizeZ, palette, Capacity(((long)nodeCount * 9 / 8 + 4096) * NODE_UINTS) / NODE_UINTS, Capacity((long)voxelCount * 9 / 8 + 65536));
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(tree.nodes.AsSpan(0, nodeCount * NODE_UINTS)));
        reader.BaseStream.ReadExactly(tree.voxels.AsSpan(0, voxelCount));
        tree.nodeCount = nodeCount;
        tree.voxelCount = voxelCount;
        tree.rebuilt = true;
        return tree;
    }
}
