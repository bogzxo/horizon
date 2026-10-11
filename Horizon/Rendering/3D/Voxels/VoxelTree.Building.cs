using System.Numerics;

namespace Horizon.Rendering.Voxels;

/// <summary>A voxel to be changed, for <see cref="VoxelTree.SetMany"/>.</summary>
public readonly record struct VoxelEdit(int X, int Y, int Z, byte Material);

/// <summary>A chunk built off to the side, see <see cref="VoxelTree.BuildChunk"/>. Nothing in it if it was all air.</summary>
public sealed class VoxelChunk(int x, int y, int z)
{
    public int X { get; } = x;
    public int Y { get; } = y;
    public int Z { get; } = z;

    // Rented, longer than what's in them
    internal uint[]? Nodes;
    internal byte[]? Voxels;
    internal int NodeLength, VoxelLength;
    internal NodeRecord Root;

    /// <summary>How much memory it is, in bytes.</summary>
    public long Bytes => NodeLength * 4L + VoxelLength;

    /// <summary>Gives the arrays back to the pool, once they've been copied into the tree.</summary>
    internal void Release()
    {
        if (Nodes is not null) System.Buffers.ArrayPool<uint>.Shared.Return(Nodes);
        if (Voxels is not null) System.Buffers.ArrayPool<byte>.Shared.Return(Voxels);
        Nodes = null;
        Voxels = null;
    }
}

public sealed partial class VoxelTree
{
    // The colour of an sRGB byte in linear light, for averaging colours the way light adds up
    private static readonly float[] Linear = BuildLinear();

    private static float[] BuildLinear()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++) table[i] = VoxelPalette.ToLinear((byte)i);
        return table;
    }

    /// <summary>
    /// Makes a world, every chunk of it from the generator, on every core there is. <paramref name="progress"/>
    /// hears how far along it is, 0 to 1. A 2048 by 256 by 2048 world is a couple of seconds on six cores.
    /// </summary>
    public static VoxelTree Generate(int sizeX, int sizeY, int sizeZ, VoxelPalette palette, IVoxelGenerator generator,
        IProgress<float>? progress = null, CancellationToken token = default)
    {
        if (sizeX % CHUNK != 0 || sizeY % CHUNK != 0 || sizeZ % CHUNK != 0)
            throw new ArgumentException($"A voxel world is a whole number of chunks ({CHUNK}) each way, not {sizeX} by {sizeY} by {sizeZ}.");

        int chunksX = sizeX / CHUNK, chunksY = sizeY / CHUNK, chunksZ = sizeZ / CHUNK;
        var results = new ChunkResult[chunksX * chunksY * chunksZ];
        int columns = chunksX * chunksZ, finished = 0;

        Parallel.For(0, columns, new ParallelOptions { CancellationToken = token },
            () => (Builder: new ChunkBuilder(palette), Dense: new byte[CHUNK_VOLUME]),
            (column, _, local) =>
            {
                int cx = column % chunksX, cz = column / chunksX;
                for (int cy = 0; cy < chunksY; cy++)
                {
                    Array.Clear(local.Dense);
                    if (!generator.Generate(cx, cy, cz, local.Dense)) continue;

                    NodeRecord root = local.Builder.Build(local.Dense);
                    if (root.IsEmpty) continue;

                    results[ChunkIndex(cx, cy, cz, chunksX, chunksZ)] = new ChunkResult(
                        local.Builder.Nodes.AsSpan(0, local.Builder.NodeCount * NODE_UINTS).ToArray(),
                        local.Builder.Voxels.AsSpan(0, local.Builder.VoxelCount).ToArray(),
                        root);
                }

                int done = Interlocked.Increment(ref finished);
                if (progress is not null && (done & 31) == 0) progress.Report(0.9f * done / columns);
                return local;
            },
            _ => { });

        // One array of everything, the chunks one after the other with where their children are moved along to match
        long nodeTotal = 1, voxelTotal = 0;
        foreach (ChunkResult result in results)
        {
            if (result.Nodes is null) continue;
            nodeTotal += result.Nodes.Length / NODE_UINTS;
            voxelTotal += result.Voxels.Length;
        }

        // The levels above the chunks are a sliver of it, and room to grow keeps the first few changes from copying the lot
        long upper = (long)chunksX * chunksY * chunksZ / 2 + 64;
        if (voxelTotal > Array.MaxLength || (nodeTotal + upper) * NODE_UINTS > Array.MaxLength) throw TooBig();
        var tree = new VoxelTree(sizeX, sizeY, sizeZ, palette,
            Capacity(((nodeTotal + upper) * 9 / 8 + 4096) * NODE_UINTS) / NODE_UINTS,
            Capacity(voxelTotal * 9 / 8 + 65536));
        tree.nodeCount = 1;

        var chunkRoots = new NodeRecord[results.Length];
        for (int i = 0; i < results.Length; i++)
        {
            ChunkResult result = results[i];
            if (result.Nodes is null) continue;

            int nodeBase = tree.nodeCount, voxelBase = tree.voxelCount;
            result.Nodes.CopyTo(tree.nodes.AsSpan(nodeBase * NODE_UINTS));
            result.Voxels.CopyTo(tree.voxels.AsSpan(voxelBase));
            tree.nodeCount += result.Nodes.Length / NODE_UINTS;
            tree.voxelCount += result.Voxels.Length;

            Relocate(tree.nodes.AsSpan(nodeBase * NODE_UINTS, result.Nodes.Length), (uint)nodeBase, (uint)voxelBase);
            chunkRoots[i] = Relocate(result.Root, (uint)nodeBase, (uint)voxelBase);
            results[i] = default;

            if (progress is not null && (i & 255) == 0) progress.Report(0.9f + 0.08f * i / results.Length);
        }

        NodeRecord root = tree.BuildUpper(tree.Depth, 0, 0, 0, chunkRoots, chunksX, chunksY, chunksZ);
        root.Write(tree.nodes.AsSpan(0, NODE_UINTS));

        // It all goes up in one go, nobody needs a list of what changed
        tree.changedNodes.Clear();
        tree.changedVoxels.Clear();
        tree.rebuilt = true;

        progress?.Report(1.0f);
        return tree;
    }

    private static int ChunkIndex(int cx, int cy, int cz, int chunksX, int chunksZ) => cx + cz * chunksX + cy * chunksX * chunksZ;

    /// <summary>Helper method to move where every node of a chunk's says its children are, to where the chunk landed in the big arrays.</summary>
    private static void Relocate(Span<uint> records, uint nodeBase, uint voxelBase)
    {
        for (int i = 0; i < records.Length; i += NODE_UINTS)
        {
            uint info = records[i + 3];
            if ((info & UNIFORM) != 0 || (records[i] | records[i + 1]) == 0) continue;
            records[i + 2] += (info & LEAF) != 0 ? voxelBase : nodeBase;
        }
    }

    private static NodeRecord Relocate(NodeRecord record, uint nodeBase, uint voxelBase)
    {
        if (record.IsEmpty || record.IsUniform) return record;
        return record with { Child = record.Child + (record.IsLeaf ? voxelBase : nodeBase) };
    }

    /// <summary>Helper method to build the levels over the chunks, from a level down to them.</summary>
    private NodeRecord BuildUpper(int level, int ox, int oy, int oz, NodeRecord[] chunkRoots, int chunksX, int chunksY, int chunksZ)
    {
        if (level == CHUNK_LEVEL)
        {
            int cx = ox / CHUNK, cy = oy / CHUNK, cz = oz / CHUNK;
            if (cx >= chunksX || cy >= chunksY || cz >= chunksZ) return NodeRecord.Empty;
            return chunkRoots[ChunkIndex(cx, cy, cz, chunksX, chunksZ)];
        }

        // Nothing of the world in here at all, the root of a world that isn't a cube is mostly this
        if (ox >= SizeX || oy >= SizeY || oz >= SizeZ) return NodeRecord.Empty;

        int size = 1 << (2 * (level - 1));
        Span<NodeRecord> children = stackalloc NodeRecord[64];
        for (int bit = 0; bit < 64; bit++)
            children[bit] = BuildUpper(level - 1, ox + (bit & 3) * size, oy + (bit >> 4) * size, oz + ((bit >> 2) & 3) * size, chunkRoots, chunksX, chunksY, chunksZ);

        return Combine(children);
    }

    /// <summary>
    /// Helper method to make a node of 64 children that are already in place somewhere, putting the ones that are
    /// there next to each other at the end of the array. All of them one solid material and it is that material instead.
    /// </summary>
    private NodeRecord Combine(ReadOnlySpan<NodeRecord> children)
    {
        ulong mask = Summarize(children, kinds, out uint info, out bool uniform, out uint material);
        if (mask == 0) return NodeRecord.Empty;
        if (uniform) return new NodeRecord(ulong.MaxValue, material, info | UNIFORM);

        Span<uint> packed = stackalloc uint[64 * NODE_UINTS];
        int count = 0;
        for (int bit = 0; bit < 64; bit++)
        {
            if ((mask & (1ul << bit)) == 0) continue;
            children[bit].Write(packed[(count * NODE_UINTS)..]);
            count++;
        }

        int start = AppendNodes(packed[..(count * NODE_UINTS)]);
        return new NodeRecord(mask, (uint)start, info);
    }

    /// <summary>
    /// Helper method for what a node of these children is. Which of them are there, what the node looks like from far
    /// away, and whether it is one solid material all the way through. The colour from far away is the colour seen
    /// from above, the top child of every column of them, because grass on top of dirt is green from a plane and not
    /// the brown of the average. Foliage doesn't count for it, a meadow is the colour of the grass it is on.
    /// </summary>
    internal static ulong Summarize(ReadOnlySpan<NodeRecord> children, VoxelKind[] kinds, out uint info, out bool uniform, out uint material)
    {
        ulong mask = 0;
        uint flags = 0;
        bool allSoft = true;
        uniform = true;
        material = 0;

        for (int bit = 0; bit < 64; bit++)
        {
            NodeRecord child = children[bit];
            if (child.IsEmpty)
            {
                uniform = false;
                continue;
            }

            mask |= 1ul << bit;
            flags |= child.Info & GLOW;
            if (!child.IsSoft) allSoft = false;

            if (!child.IsUniform || bit > 0 && child.Child != material) uniform = false;
            material = child.Child;
        }

        if (uniform && !Collapses(kinds[(byte)material])) uniform = false;

        // The colour of the top of every column of children
        Vector3 sum = Vector3.Zero;
        int coloured = 0, wet = 0;
        for (int column = 0; column < 16; column++)
        {
            for (int y = 3; y >= 0; y--)
            {
                NodeRecord child = children[column | y << 4];
                if (child.IsEmpty || child.IsSoft) continue;

                uint c = child.Info;
                sum += new Vector3(Linear[c & 0xFF], Linear[(c >> 8) & 0xFF], Linear[(c >> 16) & 0xFF]);
                coloured++;
                if ((c & LIQUID) != 0) wet++;
                break;
            }
        }

        if (coloured > 0) sum /= coloured;
        if (wet * 2 > coloured) flags |= LIQUID;
        if (allSoft && mask != 0) flags |= SOFT;

        info = VoxelPalette.PackColor(sum) | flags;
        return mask;
    }

    /// <summary>Whether a block of only this can be kept as one node. Leaves and foliage have insides to them, they can't.</summary>
    internal static bool Collapses(VoxelKind kind) => kind is VoxelKind.Solid or VoxelKind.Glass or VoxelKind.Liquid;

    /* A chunk at a time, for a world made while it's played in (VoxelStreamer) */

    // Every thread that builds chunks has a builder and a chunk of voxels of its own
    [ThreadStatic] private static ChunkBuilder? threadBuilder;
    [ThreadStatic] private static VoxelPalette? threadPalette;
    [ThreadStatic] private static byte[]? threadDense;

    /// <summary>
    /// Makes a chunk out of a generator, ready to go into the tree with <see cref="Insert"/>. From any thread, as many
    /// at once as there are, nothing of the tree is touched.
    /// </summary>
    public VoxelChunk BuildChunk(IVoxelGenerator generator, int cx, int cy, int cz)
    {
        if (threadBuilder is null || !ReferenceEquals(threadPalette, Palette))
        {
            threadBuilder = new ChunkBuilder(Palette);
            threadPalette = Palette;
        }

        threadDense ??= new byte[CHUNK_VOLUME];
        Array.Clear(threadDense);

        var chunk = new VoxelChunk(cx, cy, cz);
        if (!generator.Generate(cx, cy, cz, threadDense)) return chunk;

        NodeRecord root = threadBuilder.Build(threadDense);
        if (root.IsEmpty) return chunk;

        // Out of a pool, and back into it once they're in the tree. Made new every chunk they were a hundred megabytes a
        // second of garbage, and the collections that went with it held up every thread, the render thread too
        chunk.NodeLength = threadBuilder.NodeCount * NODE_UINTS;
        chunk.VoxelLength = threadBuilder.VoxelCount;
        chunk.Nodes = System.Buffers.ArrayPool<uint>.Shared.Rent(Math.Max(chunk.NodeLength, 1));
        chunk.Voxels = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(chunk.VoxelLength, 1));
        threadBuilder.Nodes.AsSpan(0, chunk.NodeLength).CopyTo(chunk.Nodes);
        threadBuilder.Voxels.AsSpan(0, chunk.VoxelLength).CopyTo(chunk.Voxels);
        chunk.Root = root;
        return chunk;
    }

    /// <summary>
    /// Puts chunks made by <see cref="BuildChunk"/> into the tree, in place of whatever was there, and the nodes over
    /// them told where they are. The changes go to the renderer like any other.
    /// </summary>
    public void Insert(ReadOnlySpan<VoxelChunk> chunks)
    {
        lock (SyncRoot)
        {
            foreach (VoxelChunk chunk in chunks)
            {
                NodeRecord record = NodeRecord.Empty;
                if (chunk.Nodes is { } chunkNodes && chunk.Voxels is { } chunkVoxels)
                {
                    int nodeBase = AppendNodes(chunkNodes.AsSpan(0, chunk.NodeLength));
                    int voxelBase = AppendVoxels(chunkVoxels.AsSpan(0, chunk.VoxelLength));
                    Relocate(nodes.AsSpan(nodeBase * NODE_UINTS, chunk.NodeLength), (uint)nodeBase, (uint)voxelBase);
                    record = Relocate(chunk.Root, (uint)nodeBase, (uint)voxelBase);
                    chunk.Release();
                }

                NodeRecord old = FindChunk(chunk.X, chunk.Y, chunk.Z);
                if (old.IsEmpty && record.IsEmpty) continue;

                Garbage += CountNodes(old);
                NodeRecord root = Rewrite(ReadNode(0), Depth, 0, 0, 0, chunk.X * CHUNK, chunk.Y * CHUNK, chunk.Z * CHUNK, record);
                WriteNode(0, root);
            }

            Version++;
        }
    }

    /* Changes */

    /// <summary>Changes one voxel. Builds the chunk it is in again, a millisecond or two, so a lot of them go through <see cref="SetMany"/>.</summary>
    public void Set(int x, int y, int z, byte material) => SetMany([new VoxelEdit(x, y, z, material)]);

    // One builder for changes, they are made on one thread at a time
    private ChunkBuilder? editBuilder;
    private byte[]? editDense;

    /// <summary>Changes many voxels at once, every chunk they are in built again once.</summary>
    public void SetMany(ReadOnlySpan<VoxelEdit> edits)
    {
        if (edits.IsEmpty) return;

        // By chunk, so a chunk is taken apart and put back together once however many of its voxels change
        var byChunk = new Dictionary<(int, int, int), List<VoxelEdit>>();
        foreach (VoxelEdit edit in edits)
        {
            if ((uint)edit.X >= (uint)SizeX || (uint)edit.Y >= (uint)SizeY || (uint)edit.Z >= (uint)SizeZ) continue;

            var key = (edit.X / CHUNK, edit.Y / CHUNK, edit.Z / CHUNK);
            if (!byChunk.TryGetValue(key, out var list)) byChunk[key] = list = [];
            list.Add(edit);
        }

        lock (SyncRoot)
        {
            editBuilder ??= new ChunkBuilder(Palette);
            editDense ??= new byte[CHUNK_VOLUME];

            foreach (var ((cx, cy, cz), list) in byChunk)
            {
                Array.Clear(editDense);
                NodeRecord old = FindChunk(cx, cy, cz);
                DecodeNode(old, CHUNK_LEVEL, 0, 0, 0, editDense);

                bool changed = false;
                foreach (VoxelEdit edit in list)
                {
                    int at = (edit.X - cx * CHUNK) + (edit.Z - cz * CHUNK) * CHUNK + (edit.Y - cy * CHUNK) * CHUNK * CHUNK;
                    if (editDense[at] == edit.Material) continue;
                    editDense[at] = edit.Material;
                    changed = true;
                }

                if (!changed) continue;

                Garbage += CountNodes(old);

                // The chunk again, at the end of the arrays
                NodeRecord built = editBuilder.Build(editDense);
                NodeRecord chunk = NodeRecord.Empty;
                if (!built.IsEmpty)
                {
                    int nodeBase = AppendNodes(editBuilder.Nodes.AsSpan(0, editBuilder.NodeCount * NODE_UINTS));
                    int voxelBase = AppendVoxels(editBuilder.Voxels.AsSpan(0, editBuilder.VoxelCount));
                    Relocate(nodes.AsSpan(nodeBase * NODE_UINTS, editBuilder.NodeCount * NODE_UINTS), (uint)nodeBase, (uint)voxelBase);
                    chunk = Relocate(built, (uint)nodeBase, (uint)voxelBase);
                }

                // And the nodes over it told where it is now
                NodeRecord root = Rewrite(ReadNode(0), Depth, 0, 0, 0, cx * CHUNK, cy * CHUNK, cz * CHUNK, chunk);
                WriteNode(0, root);
            }

            Version++;
        }
    }

    /// <summary>Helper method to find the node of a chunk, empty if there's nothing there. A chunk inside something solid comes back as that.</summary>
    private NodeRecord FindChunk(int cx, int cy, int cz)
    {
        int x = cx * CHUNK, y = cy * CHUNK, z = cz * CHUNK;
        NodeRecord node = ReadNode(0);
        for (int level = Depth; level > CHUNK_LEVEL; level--)
        {
            if (node.IsEmpty) return NodeRecord.Empty;
            if (node.IsUniform) return node;

            int shift = 2 * (level - 1);
            int bit = ((x >> shift) & 3) | (((z >> shift) & 3) << 2) | (((y >> shift) & 3) << 4);
            if ((node.Mask & (1ul << bit)) == 0) return NodeRecord.Empty;
            node = ReadNode((int)node.Child + Rank(node.Mask, bit));
        }

        return node;
    }

    /// <summary>Helper method to write out the voxels under a node into a chunk's worth of them, the node at a place in the chunk.</summary>
    private void DecodeNode(NodeRecord node, int level, int ox, int oy, int oz, Span<byte> dense)
    {
        if (node.IsEmpty) return;

        int size = 1 << (2 * level);
        if (node.IsUniform)
        {
            byte material = (byte)node.Child;
            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                    dense.Slice(ox + (oz + z) * CHUNK + (oy + y) * CHUNK * CHUNK, size).Fill(material);
            }

            return;
        }

        int childSize = size >> 2;
        ulong mask = node.Mask;
        int rank = 0;
        while (mask != 0)
        {
            int bit = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int x = ox + (bit & 3) * childSize, y = oy + (bit >> 4) * childSize, z = oz + ((bit >> 2) & 3) * childSize;

            if (node.IsLeaf) dense[x + z * CHUNK + y * CHUNK * CHUNK] = voxels[node.Child + rank];
            else DecodeNode(ReadNode((int)node.Child + rank), level - 1, x, y, z, dense);
            rank++;
        }
    }

    /// <summary>Helper method for how many nodes there are under one, for how much garbage it leaves behind.</summary>
    private int CountNodes(NodeRecord node)
    {
        if (node.IsEmpty || node.IsUniform || node.IsLeaf) return 0;

        int count = BitOperations.PopCount(node.Mask);
        for (int i = 0; i < BitOperations.PopCount(node.Mask); i++) count += CountNodes(ReadNode((int)node.Child + i));
        return count;
    }

    /// <summary>
    /// Helper method to put a chunk's new node in under a node, and say what that node is now. If the same children are
    /// there as before the chunk's node (or the one over it) is written over in place and nothing moves, if one came or
    /// went the children are put down again at the end of the array. A node that was one solid material is split up
    /// into 64 of it first.
    /// </summary>
    private NodeRecord Rewrite(NodeRecord node, int level, int ox, int oy, int oz, int tx, int ty, int tz, NodeRecord chunk)
    {
        if (level == CHUNK_LEVEL) return chunk;

        Span<NodeRecord> children = stackalloc NodeRecord[64];
        bool wasUniform = !node.IsEmpty && node.IsUniform;
        if (wasUniform)
        {
            NodeRecord inside = new(ulong.MaxValue, node.Child, node.Info);
            children.Fill(inside);
        }
        else
        {
            ulong bits = node.Mask;
            int rank = 0;
            while (bits != 0)
            {
                int b = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                children[b] = ReadNode((int)node.Child + rank++);
            }
        }

        int shift = 2 * (level - 1), size = 1 << shift;
        int bit = ((tx >> shift) & 3) | (((tz >> shift) & 3) << 2) | (((ty >> shift) & 3) << 4);
        children[bit] = Rewrite(children[bit], level - 1,
            ox + (bit & 3) * size, oy + (bit >> 4) * size, oz + ((bit >> 2) & 3) * size, tx, ty, tz, chunk);

        ulong mask = Summarize(children, kinds, out uint info, out _, out _);
        if (mask == 0)
        {
            if (!wasUniform) Garbage += BitOperations.PopCount(node.Mask);
            return NodeRecord.Empty;
        }

        // The same children as before, the one that changed is written over where it is
        if (!wasUniform && mask == node.Mask)
        {
            WriteNode((int)node.Child + Rank(mask, bit), children[bit]);
            return new NodeRecord(mask, node.Child, info);
        }

        if (!wasUniform) Garbage += BitOperations.PopCount(node.Mask);

        Span<uint> packed = stackalloc uint[64 * NODE_UINTS];
        int count = 0;
        for (int b = 0; b < 64; b++)
        {
            if ((mask & (1ul << b)) == 0) continue;
            children[b].Write(packed[(count * NODE_UINTS)..]);
            count++;
        }

        int start = AppendNodes(packed[..(count * NODE_UINTS)]);
        return new NodeRecord(mask, (uint)start, info);
    }

    /// <summary>What a chunk's builder left, nodes and voxels with children counted from the start of the chunk's own.</summary>
    private readonly record struct ChunkResult(uint[] Nodes, byte[] Voxels, NodeRecord Root);

    /// <summary>
    /// Builds the nodes of one chunk out of its voxels, 64 a side, into arrays of its own, which the caller puts
    /// wherever they go and moves the children along to match (<see cref="Relocate(Span{uint}, uint, uint)"/>). Children
    /// are put down before whoever points at them, so a chunk is one pass from the bottom up.
    /// </summary>
    internal sealed class ChunkBuilder
    {
        private readonly VoxelKind[] kinds = new VoxelKind[VoxelPalette.CAPACITY];
        private readonly uint[] colors = new uint[VoxelPalette.CAPACITY];
        private readonly bool[] glows = new bool[VoxelPalette.CAPACITY];

        public uint[] Nodes = new uint[4096 * NODE_UINTS];
        public int NodeCount;
        public byte[] Voxels = new byte[CHUNK_VOLUME];
        public int VoxelCount;

        public ChunkBuilder(VoxelPalette palette)
        {
            for (int i = 0; i < palette.Count; i++)
            {
                VoxelMaterial m = palette[i];
                kinds[i] = m.Kind;
                colors[i] = VoxelPalette.PackColor(m.Color);
                glows[i] = m.Emission.LengthSquared() > 0.0f;
            }
        }

        public NodeRecord Build(ReadOnlySpan<byte> dense)
        {
            NodeCount = 0;
            VoxelCount = 0;
            return BuildNode(dense, CHUNK_LEVEL, 0, 0, 0);
        }

        private NodeRecord BuildNode(ReadOnlySpan<byte> dense, int level, int ox, int oy, int oz)
        {
            if (level == 1) return BuildLeaf(dense, ox, oy, oz);

            int size = 1 << (2 * (level - 1));
            Span<NodeRecord> children = stackalloc NodeRecord[64];
            for (int bit = 0; bit < 64; bit++)
                children[bit] = BuildNode(dense, level - 1, ox + (bit & 3) * size, oy + (bit >> 4) * size, oz + ((bit >> 2) & 3) * size);

            ulong mask = Summarize(children, kinds, out uint info, out bool uniform, out uint material);
            if (mask == 0) return NodeRecord.Empty;
            if (uniform) return new NodeRecord(ulong.MaxValue, material, info | UNIFORM);

            int start = NodeCount;
            int count = BitOperations.PopCount(mask);
            if ((NodeCount + count) * NODE_UINTS > Nodes.Length) Array.Resize(ref Nodes, Nodes.Length * 2);

            for (int bit = 0; bit < 64; bit++)
            {
                if ((mask & (1ul << bit)) == 0) continue;
                children[bit].Write(Nodes.AsSpan(NodeCount * NODE_UINTS));
                NodeCount++;
            }

            return new NodeRecord(mask, (uint)start, info);
        }

        private NodeRecord BuildLeaf(ReadOnlySpan<byte> dense, int ox, int oy, int oz)
        {
            ulong mask = 0;
            Span<byte> found = stackalloc byte[64];
            int count = 0;
            bool same = true;
            bool glow = false, allSoft = true;

            for (int bit = 0; bit < 64; bit++)
            {
                int x = ox + (bit & 3), z = oz + ((bit >> 2) & 3), y = oy + (bit >> 4);
                byte m = dense[x + z * CHUNK + y * CHUNK * CHUNK];
                if (m == 0) continue;

                mask |= 1ul << bit;
                if (count > 0 && m != found[0]) same = false;
                found[count++] = m;
                glow |= glows[m];
                if (kinds[m] != VoxelKind.Foliage) allSoft = false;
            }

            if (mask == 0) return NodeRecord.Empty;

            // The colour from above, the top voxel of every column that isn't foliage
            Vector3 sum = Vector3.Zero;
            int coloured = 0, wet = 0;
            for (int column = 0; column < 16; column++)
            {
                int x = ox + (column & 3), z = oz + (column >> 2);
                for (int y = 3; y >= 0; y--)
                {
                    byte m = dense[x + z * CHUNK + (oy + y) * CHUNK * CHUNK];
                    if (m == 0 || kinds[m] == VoxelKind.Foliage) continue;

                    uint c = colors[m];
                    sum += new Vector3(Linear[c & 0xFF], Linear[(c >> 8) & 0xFF], Linear[(c >> 16) & 0xFF]);
                    coloured++;
                    if (kinds[m] == VoxelKind.Liquid) wet++;
                    break;
                }
            }

            // All foliage has no colour of its own from far away, it is the grass under it
            if (allSoft)
            {
                for (int i = 0; i < count; i++)
                {
                    uint c = colors[found[i]];
                    sum += new Vector3(Linear[c & 0xFF], Linear[(c >> 8) & 0xFF], Linear[(c >> 16) & 0xFF]);
                }

                coloured = count;
            }

            if (coloured > 0) sum /= coloured;

            uint info = VoxelPalette.PackColor(sum);
            if (glow) info |= GLOW;
            if (wet * 2 > coloured) info |= LIQUID;
            if (allSoft) info |= SOFT;

            if (mask == ulong.MaxValue && same && Collapses(kinds[found[0]]))
                return new NodeRecord(ulong.MaxValue, found[0], info | UNIFORM);

            int start = VoxelCount;
            found[..count].CopyTo(Voxels.AsSpan(VoxelCount));
            VoxelCount += count;
            return new NodeRecord(mask, (uint)start, info | LEAF);
        }
    }
}
