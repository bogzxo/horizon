using System.Numerics;

using Horizon.Logging;

namespace Horizon.Rendering.Voxels;

/// <summary>
/// Makes a world while it's being played in. The tree starts empty and every column of chunks is made on a thread of
/// its own and put into it (<see cref="VoxelTree.BuildChunk"/>, <see cref="VoxelTree.Insert"/>), the nearest to
/// <see cref="Focus"/> first, so the world fills in round the player in rings, outwards, and they're never waiting for
/// the far side of it to start playing. Where the player goes the rings follow. The renderer draws it as it comes, a
/// column at a time, the way it draws any change.
/// <code>
/// var tree = VoxelTree.Empty(size, 256, size, palette, VoxelStreamer.EstimateNodes(size, size), VoxelStreamer.EstimateVoxels(size, size));
/// var streamer = new VoxelStreamer(tree, generator);
/// streamer.MakeAround(spawn, 3);   // the ground under the player, before they're put on it
/// streamer.Start();
/// // every update
/// streamer.Focus = player.Position;
/// </code>
/// <para>
/// The threads run below the others, the render thread and the simulation come first. A world that was saved half
/// made carries on from where it was (<see cref="Done"/> going into the constructor).
/// </para>
/// </summary>
public sealed class VoxelStreamer : IDisposable
{
    private readonly VoxelTree tree;
    private readonly IVoxelGenerator generator;
    private readonly int columnsX, columnsZ, chunksY;
    private readonly bool[] done, claimed;
    private readonly List<int> remaining = [];
    private readonly Lock picking = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Thread> threads = [];

    private int cursor, columnsDone;
    private Vector3 focus, sortedFor;
    private bool sorted;

    /// <summary>Where the player is, the columns nearest to it are made first. From any thread.</summary>
    public Vector3 Focus
    {
        get
        {
            lock (picking) return focus;
        }
        set
        {
            lock (picking) focus = value;
        }
    }

    public int ColumnsTotal => columnsX * columnsZ;

    public int ColumnsDone => Volatile.Read(ref columnsDone);

    /// <summary>How much of the world is made, 0 to 1.</summary>
    public float Progress => ColumnsTotal == 0 ? 1.0f : (float)ColumnsDone / ColumnsTotal;

    public bool IsComplete => ColumnsDone >= ColumnsTotal;

    /// <summary>Which columns are made, x + z * columns across, for saving a world that isn't finished.</summary>
    public bool[] Done
    {
        get
        {
            lock (picking) return (bool[])done.Clone();
        }
    }

    /// <param name="threads">How many threads make the world, all the cores but two if it doesn't say.</param>
    /// <param name="alreadyDone">The columns made already, for a world that was saved half made.</param>
    public VoxelStreamer(VoxelTree tree, IVoxelGenerator generator, int threads = 0, bool[]? alreadyDone = null)
    {
        this.tree = tree;
        this.generator = generator;
        columnsX = tree.SizeX / VoxelTree.CHUNK;
        columnsZ = tree.SizeZ / VoxelTree.CHUNK;
        chunksY = tree.SizeY / VoxelTree.CHUNK;

        done = new bool[columnsX * columnsZ];
        claimed = new bool[done.Length];
        if (alreadyDone is not null && alreadyDone.Length == done.Length) Array.Copy(alreadyDone, done, done.Length);

        for (int i = 0; i < done.Length; i++)
        {
            if (done[i])
            {
                claimed[i] = true;
                columnsDone++;
            }
            else remaining.Add(i);
        }

        int count = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount - 2);
        for (int i = 0; i < count; i++)
            this.threads.Add(new Thread(Work) { Name = $"Voxel streamer {i}", IsBackground = true, Priority = ThreadPriority.BelowNormal });
    }

    /// <summary>How many nodes a world of this size usually comes to, for <see cref="VoxelTree.Empty(int, int, int, VoxelPalette, long, long)"/>.</summary>
    public static long EstimateNodes(int sizeX, int sizeZ) => (long)sizeX * sizeZ * 3 / 2 + 65536;

    /// <summary>And how many voxel bytes.</summary>
    public static long EstimateVoxels(int sizeX, int sizeZ) => (long)sizeX * sizeZ * 26 + (1 << 20);

    /// <summary>Whether the column a point of the world is in is made yet. Nothing in a column that isn't is real yet.</summary>
    public bool IsReady(int x, int z)
    {
        int cx = x / VoxelTree.CHUNK, cz = z / VoxelTree.CHUNK;
        if ((uint)cx >= (uint)columnsX || (uint)cz >= (uint)columnsZ) return false;
        return Volatile.Read(ref done[cx + cz * columnsX]);
    }

    /// <summary>
    /// Makes the columns within a radius (in chunks) of a point now, on every core, and comes back once they're in.
    /// For the ground under somebody about to be put on it.
    /// </summary>
    public void MakeAround(Vector3 at, int radius)
    {
        int ox = (int)at.X / VoxelTree.CHUNK, oz = (int)at.Z / VoxelTree.CHUNK;
        var wanted = new List<int>();
        lock (picking)
        {
            for (int z = oz - radius; z <= oz + radius; z++)
            for (int x = ox - radius; x <= ox + radius; x++)
            {
                if ((uint)x >= (uint)columnsX || (uint)z >= (uint)columnsZ) continue;
                int column = x + z * columnsX;
                if (claimed[column]) continue;
                claimed[column] = true;
                wanted.Add(column);
            }
        }

        Parallel.ForEach(wanted, Make);
    }

    /// <summary>Sets the threads going on the rest of the world.</summary>
    public void Start()
    {
        foreach (Thread thread in threads) thread.Start();
    }

    private void Work()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                int column = Next();
                if (column < 0) return;
                Make(column);
            }
        }
        catch (Exception e)
        {
            Log.Error($"[Voxel streamer] Making the world went wrong: {e}");
        }
    }

    /// <summary>Helper method for the nearest column to the focus that nobody has started on, -1 when there are none.</summary>
    private int Next()
    {
        lock (picking)
        {
            // In order of how far they are from the focus, sorted again once it has moved a few chunks
            if (!sorted || Vector2.Distance(new Vector2(focus.X, focus.Z), new Vector2(sortedFor.X, sortedFor.Z)) > VoxelTree.CHUNK * 2)
            {
                remaining.RemoveAll(c => claimed[c]);
                float fx = focus.X / VoxelTree.CHUNK - 0.5f, fz = focus.Z / VoxelTree.CHUNK - 0.5f;
                int across = columnsX;
                remaining.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
                cursor = 0;
                sortedFor = focus;
                sorted = true;

                float Distance(int c)
                {
                    float dx = c % across - fx, dz = c / across - fz;
                    return dx * dx + dz * dz;
                }
            }

            while (cursor < remaining.Count)
            {
                int column = remaining[cursor++];
                if (claimed[column]) continue;
                claimed[column] = true;
                return column;
            }

            return -1;
        }
    }

    /// <summary>Helper method to make a column of chunks and put it in the tree.</summary>
    private void Make(int column)
    {
        int cx = column % columnsX, cz = column / columnsX;
        var chunks = new VoxelChunk[chunksY];
        for (int cy = 0; cy < chunksY; cy++) chunks[cy] = tree.BuildChunk(generator, cx, cy, cz);

        tree.Insert(chunks);

        lock (picking) done[column] = true;
        Interlocked.Increment(ref columnsDone);
    }

    public void Dispose()
    {
        stopping.Cancel();
        foreach (Thread thread in threads)
        {
            if (thread.IsAlive) thread.Join();
        }
    }
}
