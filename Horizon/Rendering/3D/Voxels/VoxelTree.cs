using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Physics.Colliders;

namespace Horizon.Rendering.Voxels;

/// <summary>
/// Makes the voxels of a world, a chunk (64 a side) at a time, for <see cref="VoxelTree.Generate"/>. Called from
/// many threads at once, for chunks in any order, so whatever it works out has to come from where the chunk is and
/// nothing else (a tree that hangs over into the next chunk is worked out again by that chunk, the same way).
/// </summary>
public interface IVoxelGenerator
{
    /// <summary>
    /// Fills a chunk, 64 a side, X fastest then Z then Y (<c>x + z * 64 + y * 4096</c>), the materials of the
    /// palette, 0 for air. It comes in all air. Returns false if it left it that way, which is quicker for everyone.
    /// </summary>
    bool Generate(int chunkX, int chunkY, int chunkZ, Span<byte> voxels);
}

/// <summary>Where a ray hit a voxel, which one, what it is and which face it came in through.</summary>
public readonly record struct VoxelHit(int X, int Y, int Z, byte Material, Vector3 Normal, float Distance)
{
    /// <summary>The voxel on the other side of the face that was hit, where a block placed on it goes.</summary>
    public (int X, int Y, int Z) Adjacent => (X + (int)Normal.X, Y + (int)Normal.Y, Z + (int)Normal.Z);
}

/// <summary>
/// A world of voxels, finite, kept as a sparse 64-tree, which is what the path tracer goes through and what the
/// collision asks. Every node is 16 bytes, a mask of which of its 4 x 4 x 4 children have anything in them, where its
/// children start, and what it looks like from far away. The children of a node sit next to each other and only the
/// ones that are there are kept, so the place of a child is where they start plus how many bits of the mask come
/// before its own (a popcount). The nodes at the bottom (four voxels a side) have the voxels for children, one byte
/// each, kept the same way. A node that is all one solid material all the way through (the inside of a mountain, an
/// ocean) is one node and nothing under it.
/// <para>
/// Why that and not an octree or a grid of bricks. A ray going through an octree goes down one level for every
/// halving, the 64-tree goes down a level for every quartering, so half the levels and half the reads, and one
/// 64 bit mask answers "is any of this empty" for 64 cells at once, which is what skipping air quickly is. A grid of
/// bricks is quick near and spends memory on every empty brick of the sky and a ray goes through every one of those
/// on its way across the world. Air costs nothing here, the inside of the ground next to nothing, what costs is
/// surface, which is what there is to look at. And every node knows its own colour from far away, so a ray far from
/// the camera stops at the first node smaller than the pixel it is for, which is the level of detail, for free.
/// </para>
/// <para>
/// The world is made in chunks of 64 (four levels of the tree under them, 64, 16, 4 and the voxels), on every core at
/// once (<see cref="Generate"/>). A change (<see cref="Set"/>) builds the chunk it is in again and the nodes above it,
/// the old nodes are left where they are and nobody points at them (<see cref="Garbage"/>), <see cref="Compact"/>
/// throws them out. What changed since the GPU last heard is <see cref="TakeChanges"/>, which
/// <see cref="VoxelRenderer"/> sends up a slice at a time.
/// </para>
/// Changes from the simulation thread, reads from anywhere, the renderer reads the arrays under <see cref="SyncRoot"/>.
/// </summary>
public sealed partial class VoxelTree : ICollisionField
{
    /// <summary>How big a chunk is, the unit the world is made and changed in.</summary>
    public const int CHUNK = 64;

    private const int CHUNK_LEVEL = 3;
    private const int CHUNK_VOLUME = CHUNK * CHUNK * CHUNK;

    // The flags in the top byte of a node's fourth number. The bottom three bytes are its colour from far away (sRGB)
    internal const uint LEAF = 1u << 24;
    internal const uint UNIFORM = 1u << 25;
    internal const uint LIQUID = 1u << 26;
    internal const uint GLOW = 1u << 27;
    internal const uint SOFT = 1u << 28;

    /// <summary>Nodes are four numbers.</summary>
    internal const int NODE_UINTS = 4;

    private uint[] nodes;
    private int nodeCount;
    private byte[] voxels;
    private int voxelCount;

    // What changed since the renderer last took it, in nodes and in voxel bytes
    private readonly List<(int Start, int Count)> changedNodes = [];
    private readonly List<(int Start, int Count)> changedVoxels = [];
    private bool rebuilt = true;

    // The materials by kind, looked up a lot
    private readonly VoxelKind[] kinds = new VoxelKind[VoxelPalette.CAPACITY];

    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    /// <summary>How many levels of nodes there are, the root covering 4 to this many voxels a side.</summary>
    public int Depth { get; }

    /// <summary>How many voxels a side the root covers, a power of four at least as big as the world.</summary>
    public int RootSize => 1 << (2 * Depth);

    public VoxelPalette Palette { get; }

    /// <summary>Held while the arrays are changed, and by whoever reads them from another thread.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>How many nodes there are, counting the ones nobody points at any more.</summary>
    public int NodeCount => nodeCount;

    /// <summary>How many bytes of voxels there are.</summary>
    public int VoxelBytes => voxelCount;

    /// <summary>How many nodes and voxel bytes are left over from changes, see <see cref="Compact"/>.</summary>
    public long Garbage { get; private set; }

    /// <summary>How much memory the tree takes, in bytes.</summary>
    public long MemoryBytes => (long)nodeCount * NODE_UINTS * 4 + voxelCount;

    /// <summary>Goes up by one with every change, for whoever keeps something worked out of the tree.</summary>
    public int Version { get; private set; }

    internal uint[] Nodes => nodes;

    internal byte[] Voxels => voxels;

    private VoxelTree(int sizeX, int sizeY, int sizeZ, VoxelPalette palette, int nodeCapacity, int voxelCapacity)
    {
        if (sizeX % CHUNK != 0 || sizeY % CHUNK != 0 || sizeZ % CHUNK != 0)
            throw new ArgumentException($"A voxel world is a whole number of chunks ({CHUNK}) each way, not {sizeX} by {sizeY} by {sizeZ}.");

        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        Palette = palette;

        int largest = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
        int depth = CHUNK_LEVEL + 1;
        while ((1 << (2 * depth)) < largest) depth++;
        Depth = depth;

        nodes = new uint[Math.Max(nodeCapacity, 16) * NODE_UINTS];
        voxels = new byte[Math.Max(voxelCapacity, 64)];
        RefreshKinds();
    }

    /// <summary>
    /// An empty world, all air. <paramref name="sizeX"/>, <paramref name="sizeY"/> and <paramref name="sizeZ"/> in
    /// voxels, each a whole number of chunks.
    /// </summary>
    public static VoxelTree Empty(int sizeX, int sizeY, int sizeZ, VoxelPalette palette) => Empty(sizeX, sizeY, sizeZ, palette, 1024, 1024);

    /// <summary>
    /// An empty world with room for this many nodes and voxel bytes before its arrays have to grow, for one that is
    /// going to be filled in a chunk at a time (<see cref="VoxelStreamer"/>). Every time an array grows it is copied, and
    /// a copy of a few hundred megabytes holds up whoever wants the tree in the meantime, the renderer included.
    /// </summary>
    public static VoxelTree Empty(int sizeX, int sizeY, int sizeZ, VoxelPalette palette, long nodeCapacity, long voxelCapacity)
    {
        var tree = new VoxelTree(sizeX, sizeY, sizeZ, palette, Capacity(nodeCapacity * NODE_UINTS) / NODE_UINTS, Capacity(voxelCapacity));
        tree.nodeCount = 1;
        return tree;
    }

    /// <summary>The array capacity right now, in nodes and in voxel bytes, for whoever keeps a copy that has to be as big.</summary>
    public (long Nodes, long Voxels) Room => (nodes.Length / NODE_UINTS, voxels.Length);

    /// <summary>Takes the kinds of the materials again, after the palette changed.</summary>
    public void RefreshKinds()
    {
        for (int i = 0; i < VoxelPalette.CAPACITY; i++) kinds[i] = i < Palette.Count ? Palette[i].Kind : VoxelKind.Air;
    }

    /* Reading */

    /// <summary>The material of a voxel, 0 (air) outside of the world.</summary>
    public byte Get(int x, int y, int z)
    {
        if ((uint)x >= (uint)SizeX || (uint)y >= (uint)SizeY || (uint)z >= (uint)SizeZ) return 0;

        uint[] n = nodes;
        int at = 0;
        for (int level = Depth; level >= 1; level--)
        {
            ulong mask = Mask(n, at);
            uint info = n[at * NODE_UINTS + 3];
            if (mask == 0) return 0;
            if ((info & UNIFORM) != 0) return (byte)n[at * NODE_UINTS + 2];

            int shift = 2 * (level - 1);
            int bit = ((x >> shift) & 3) | (((z >> shift) & 3) << 2) | (((y >> shift) & 3) << 4);
            if ((mask & (1ul << bit)) == 0) return 0;

            int index = (int)n[at * NODE_UINTS + 2] + Rank(mask, bit);
            if ((info & LEAF) != 0) return voxels[index];

            at = index;
        }

        return 0;
    }

    public VoxelKind KindOf(byte material) => kinds[material];

    /// <summary>Whether a voxel is something feet stand on, solid, leaves or glass.</summary>
    public bool IsSolid(int x, int y, int z) => kinds[Get(x, y, z)] is VoxelKind.Solid or VoxelKind.Leaves or VoxelKind.Glass;

    /// <summary>
    /// The height of the highest voxel of a column that isn't air (or foliage), -1 if there is none. Slow, a lookup a
    /// voxel from the top down, for where to put somebody, not for every frame.
    /// </summary>
    public int Surface(int x, int z, bool countLiquid = true)
    {
        for (int y = SizeY - 1; y >= 0; y--)
        {
            VoxelKind kind = kinds[Get(x, y, z)];
            if (kind is VoxelKind.Air or VoxelKind.Foliage) continue;
            if (kind == VoxelKind.Liquid && !countLiquid) continue;
            return y;
        }

        return -1;
    }

    /// <summary>
    /// The first voxel along a ray, within a distance. Solid things always, foliage and liquids if asked, a player
    /// pointing at a flower wants the flower and one in water wants what is under it.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool foliage, bool liquid, out VoxelHit hit)
    {
        hit = default;
        if (direction.LengthSquared() < 1e-12f) return false;
        direction = Vector3.Normalize(direction);

        int x = (int)MathF.Floor(origin.X), y = (int)MathF.Floor(origin.Y), z = (int)MathF.Floor(origin.Z);
        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);

        // How far along the ray one voxel is each way, and how far to the first wall each way
        float deltaX = stepX != 0 ? MathF.Abs(1.0f / direction.X) : float.PositiveInfinity;
        float deltaY = stepY != 0 ? MathF.Abs(1.0f / direction.Y) : float.PositiveInfinity;
        float deltaZ = stepZ != 0 ? MathF.Abs(1.0f / direction.Z) : float.PositiveInfinity;
        float nextX = stepX != 0 ? ((stepX > 0 ? x + 1 - origin.X : origin.X - x) * deltaX) : float.PositiveInfinity;
        float nextY = stepY != 0 ? ((stepY > 0 ? y + 1 - origin.Y : origin.Y - y) * deltaY) : float.PositiveInfinity;
        float nextZ = stepZ != 0 ? ((stepZ > 0 ? z + 1 - origin.Z : origin.Z - z) * deltaZ) : float.PositiveInfinity;

        Vector3 normal = Vector3.Zero;
        float t = 0.0f;

        while (t <= maxDistance)
        {
            byte material = Get(x, y, z);
            if (material != 0)
            {
                VoxelKind kind = kinds[material];
                bool counts = kind is VoxelKind.Solid or VoxelKind.Leaves or VoxelKind.Glass
                    || kind == VoxelKind.Foliage && foliage
                    || kind == VoxelKind.Liquid && liquid;
                if (counts)
                {
                    hit = new VoxelHit(x, y, z, material, normal, t);
                    return true;
                }
            }

            if (nextX < nextY && nextX < nextZ)
            {
                x += stepX;
                t = nextX;
                nextX += deltaX;
                normal = new Vector3(-stepX, 0, 0);
            }
            else if (nextY < nextZ)
            {
                y += stepY;
                t = nextY;
                nextY += deltaY;
                normal = new Vector3(0, -stepY, 0);
            }
            else
            {
                z += stepZ;
                t = nextZ;
                nextZ += deltaZ;
                normal = new Vector3(0, 0, -stepZ);
            }

            // Out of the world on a side it isn't coming back from
            if (x < -1 && stepX <= 0 || y < -1 && stepY <= 0 || z < -1 && stepZ <= 0) return false;
            if (x > SizeX && stepX >= 0 || y > SizeY && stepY >= 0 || z > SizeZ && stepZ >= 0) return false;
        }

        return false;
    }

    /* ICollisionField */

    public void CollectSolids(in Box3 region, List<Box3> into)
    {
        int x0 = (int)MathF.Floor(region.Min.X), x1 = (int)MathF.Floor(region.Max.X);
        int y0 = (int)MathF.Floor(region.Min.Y), y1 = (int)MathF.Floor(region.Max.Y);
        int z0 = (int)MathF.Floor(region.Min.Z), z1 = (int)MathF.Floor(region.Max.Z);

        for (int y = y0; y <= y1; y++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    // The edges of the world are walls, nobody falls off it, and the floor under the bottom is solid
                    bool outside = x < 0 || z < 0 || x >= SizeX || z >= SizeZ || y < 0;
                    if (outside || IsSolid(x, y, z)) into.Add(Box3.Cell(x, y, z));
                }
            }
        }
    }

    bool ICollisionField.Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit3D hit)
    {
        if (!Raycast(origin, direction, maxDistance, false, false, out VoxelHit voxel))
        {
            hit = default;
            return false;
        }

        Vector3 point = origin + Vector3.Normalize(direction) * voxel.Distance;
        hit = new RaycastHit3D(point, voxel.Normal, voxel.Distance, null, (voxel.X, voxel.Y, voxel.Z));
        return true;
    }

    public bool IsLiquid(Vector3 point) =>
        kinds[Get((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z))] == VoxelKind.Liquid;

    /* For the renderer */

    /// <summary>
    /// What changed since the last time this was asked, in nodes and in bytes of voxels, and whether everything did
    /// (a new tree, a compacted one). Call it holding <see cref="SyncRoot"/>, and read the arrays before letting go.
    /// </summary>
    internal bool TakeChanges(List<(int Start, int Count)> nodeRanges, List<(int Start, int Count)> voxelRanges)
    {
        bool all = rebuilt;
        rebuilt = false;

        nodeRanges.AddRange(changedNodes);
        voxelRanges.AddRange(changedVoxels);
        changedNodes.Clear();
        changedVoxels.Clear();
        return all;
    }

    /* Helpers for the nodes */

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mask(uint[] n, int at) => n[at * NODE_UINTS] | (ulong)n[at * NODE_UINTS + 1] << 32;

    /// <summary>How many children come before a child, which is where it is among them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Rank(ulong mask, int bit) => BitOperations.PopCount(mask & ((1ul << bit) - 1));

    private void MarkNodes(int start, int count)
    {
        if (count <= 0) return;

        // A run that carries on from the last one is one run, the appends of a rebuild come one after the other
        if (changedNodes.Count > 0)
        {
            var last = changedNodes[^1];
            if (last.Start + last.Count == start)
            {
                changedNodes[^1] = (last.Start, last.Count + count);
                return;
            }
        }

        changedNodes.Add((start, count));
    }

    private void MarkVoxels(int start, int count)
    {
        if (count <= 0) return;
        if (changedVoxels.Count > 0)
        {
            var last = changedVoxels[^1];
            if (last.Start + last.Count == start)
            {
                changedVoxels[^1] = (last.Start, last.Count + count);
                return;
            }
        }

        changedVoxels.Add((start, count));
    }

    private int AppendNodes(ReadOnlySpan<uint> records)
    {
        int count = records.Length / NODE_UINTS;
        EnsureNodes((long)nodeCount + count);
        records.CopyTo(nodes.AsSpan(nodeCount * NODE_UINTS));
        int start = nodeCount;
        nodeCount += count;
        MarkNodes(start, count);
        return start;
    }

    private int AppendVoxels(ReadOnlySpan<byte> bytes)
    {
        EnsureVoxels((long)voxelCount + bytes.Length);
        bytes.CopyTo(voxels.AsSpan(voxelCount));
        int start = voxelCount;
        voxelCount += bytes.Length;
        MarkVoxels(start, bytes.Length);
        return start;
    }

    private void WriteNode(int at, in NodeRecord record)
    {
        record.Write(nodes.AsSpan(at * NODE_UINTS));
        MarkNodes(at, 1);
    }

    private void EnsureNodes(long count)
    {
        if (count * NODE_UINTS <= nodes.Length) return;
        if (count * NODE_UINTS > Array.MaxLength) throw TooBig();
        Array.Resize(ref nodes, Capacity(Math.Max(count * NODE_UINTS, nodes.Length * 3L / 2)));
    }

    private void EnsureVoxels(long count)
    {
        if (count <= voxels.Length) return;
        if (count > Array.MaxLength) throw TooBig();
        Array.Resize(ref voxels, Capacity(Math.Max(count, voxels.Length * 3L / 2)));
    }

    /// <summary>Helper method for how big an array can be made, as asked or as big as .NET lets an array be.</summary>
    internal static int Capacity(long wanted) => (int)Math.Clamp(wanted, 0L, Array.MaxLength);

    private static InvalidOperationException TooBig() =>
        new("The world has outgrown what one .NET array holds (two billion voxels or half a billion nodes). Make it smaller, or with less in it.");

    internal NodeRecord ReadNode(int at) => NodeRecord.Read(nodes.AsSpan(at * NODE_UINTS));
}

/// <summary>One node, as it sits in the array. The mask of its children, where they start (or its material, if it is all one), and its colour and flags.</summary>
internal readonly record struct NodeRecord(ulong Mask, uint Child, uint Info)
{
    public static readonly NodeRecord Empty = default;

    public bool IsEmpty => Mask == 0;

    public bool IsLeaf => (Info & VoxelTree.LEAF) != 0;

    public bool IsUniform => (Info & VoxelTree.UNIFORM) != 0;

    public bool IsSoft => (Info & VoxelTree.SOFT) != 0;

    public static NodeRecord Read(ReadOnlySpan<uint> at) => new(at[0] | (ulong)at[1] << 32, at[2], at[3]);

    public void Write(Span<uint> at)
    {
        at[0] = (uint)Mask;
        at[1] = (uint)(Mask >> 32);
        at[2] = Child;
        at[3] = Info;
    }
}
