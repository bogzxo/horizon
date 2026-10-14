using System.Numerics;

using Horizon.Core.Components;
using Horizon.Physics.Colliders;
using Horizon.Rendering.Voxels;

namespace Horizon.Tests;

public class VoxelTreeTests
{
    private static readonly VoxelPalette Palette = MakePalette(out Stone, out Dirt, out Grass, out Water, out Flower, out Leaves);
    private static byte Stone, Dirt, Grass, Water, Flower, Leaves;

    private static VoxelPalette MakePalette(out byte stone, out byte dirt, out byte grass, out byte water, out byte flower, out byte leaves)
    {
        var palette = new VoxelPalette();
        stone = palette.Add(VoxelMaterial.Cube("stone", new Vector3(0.4f)));
        dirt = palette.Add(VoxelMaterial.Cube("dirt", new Vector3(0.3f, 0.2f, 0.1f)));
        grass = palette.Add(VoxelMaterial.Cube("grass", new Vector3(0.2f, 0.6f, 0.1f)));
        water = palette.Add(new VoxelMaterial { Name = "water", Kind = VoxelKind.Liquid, Color = new Vector3(0.1f, 0.2f, 0.6f) });
        flower = palette.Add(new VoxelMaterial { Name = "flower", Kind = VoxelKind.Foliage, Color = new Vector3(0.9f, 0.1f, 0.1f) });
        leaves = palette.Add(new VoxelMaterial { Name = "leaves", Kind = VoxelKind.Leaves, Color = new Vector3(0.1f, 0.4f, 0.1f) });
        return palette;
    }

    /// <summary>Hills of stone, dirt and grass, a lake, flowers dotted about and a blob of leaves, all worked out from where it is.</summary>
    private sealed class Hills : IVoxelGenerator
    {
        public byte At(int x, int y, int z)
        {
            int height = 40 + (int)(10 * MathF.Sin(x * 0.07f) * MathF.Cos(z * 0.05f)) + (x * 7 + z * 13) % 3;
            if (y < height - 4) return Stone;
            if (y < height - 1) return Dirt;
            if (y < height) return height < 36 ? Dirt : Grass;
            if (y < 36) return Water;
            if (y == height && (x * 31 + z * 17) % 11 == 0) return Flower;
            if (Vector3.Distance(new Vector3(x, y, z), new Vector3(100, 60, 30)) < 6) return Leaves;
            return 0;
        }

        public bool Generate(int cx, int cy, int cz, Span<byte> voxels)
        {
            bool any = false;
            for (int y = 0; y < 64; y++)
            for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                byte m = At(cx * 64 + x, cy * 64 + y, cz * 64 + z);
                voxels[x + z * 64 + y * 4096] = m;
                any |= m != 0;
            }

            return any;
        }
    }

    private static (VoxelTree Tree, Hills Hills) Make()
    {
        var hills = new Hills();
        return (VoxelTree.Generate(256, 128, 192, Palette, hills), hills);
    }

    [Fact]
    public void Every_voxel_reads_back_as_it_was_made()
    {
        var (tree, hills) = Make();
        Assert.Equal(4, tree.Depth);
        Assert.Equal(256, tree.RootSize);

        for (int y = 0; y < tree.SizeY; y++)
        for (int z = 0; z < tree.SizeZ; z += 3)
        for (int x = 0; x < tree.SizeX; x += 5)
            Assert.Equal(hills.At(x, y, z), tree.Get(x, y, z));

        // And outside of it is air
        Assert.Equal(0, tree.Get(-1, 10, 10));
        Assert.Equal(0, tree.Get(10, 10, 192));
    }

    [Fact]
    public void Stone_all_the_way_through_is_one_node()
    {
        var (tree, _) = Make();

        // 256 x 128 x 192 voxels, most of them under the ground, in far fewer nodes than there are leaves of four
        long leavesOfFour = 256L * 128 * 192 / 64;
        Assert.True(tree.NodeCount < leavesOfFour / 4, $"{tree.NodeCount} nodes");
    }

    [Fact]
    public void A_change_reads_back_and_leaves_the_rest_alone()
    {
        var (tree, hills) = Make();

        // Dig down into the stone, through a chunk boundary, and build a tower into the sky where there was nothing
        var edits = new List<VoxelEdit>();
        for (int y = 20; y < 70; y++) edits.Add(new VoxelEdit(63, y, 64, 0));
        for (int y = 80; y < 127; y++) edits.Add(new VoxelEdit(200, y, 150, Stone));
        tree.SetMany(edits.ToArray());

        for (int y = 20; y < 70; y++) Assert.Equal(0, tree.Get(63, y, 64));
        for (int y = 80; y < 127; y++) Assert.Equal(Stone, tree.Get(200, y, 150));

        for (int y = 0; y < tree.SizeY; y++)
        for (int z = 50; z < 80; z++)
        for (int x = 50; x < 80; x++)
        {
            if (x == 63 && z == 64 && y >= 20 && y < 70) continue;
            Assert.Equal(hills.At(x, y, z), tree.Get(x, y, z));
        }

        Assert.True(tree.Garbage > 0);
    }

    [Fact]
    public void Filling_a_chunk_solid_and_digging_into_it_again_works()
    {
        var (tree, _) = Make();

        // A whole chunk of stone collapses to one node, and a hole in it splits it again
        var fill = new List<VoxelEdit>();
        for (int y = 64; y < 128; y++)
        for (int z = 64; z < 128; z++)
        for (int x = 128; x < 192; x++)
            fill.Add(new VoxelEdit(x, y, z, Stone));
        tree.SetMany(fill.ToArray());
        Assert.Equal(Stone, tree.Get(150, 100, 100));

        tree.Set(150, 100, 100, 0);
        Assert.Equal(0, tree.Get(150, 100, 100));
        Assert.Equal(Stone, tree.Get(151, 100, 100));
        Assert.Equal(Stone, tree.Get(150, 101, 100));
    }

    [Fact]
    public void Compacting_and_saving_keep_every_voxel()
    {
        var (tree, hills) = Make();
        tree.Set(10, 100, 10, Stone);
        tree.Set(11, 41, 11, 0);
        int before = tree.NodeCount;

        tree.Compact();
        Assert.Equal(0, tree.Garbage);
        Assert.True(tree.NodeCount <= before);

        using var stream = new MemoryStream();
        tree.Save(stream);
        stream.Position = 0;
        VoxelTree loaded = VoxelTree.Load(stream, Palette);

        for (int y = 0; y < tree.SizeY; y += 2)
        for (int z = 0; z < tree.SizeZ; z += 7)
        for (int x = 0; x < tree.SizeX; x += 3)
            Assert.Equal(tree.Get(x, y, z), loaded.Get(x, y, z));

        Assert.Equal(Stone, loaded.Get(10, 100, 10));
        Assert.Equal(0, loaded.Get(11, 41, 11));
    }

    [Fact]
    public void A_ray_down_lands_on_the_ground_and_through_the_flowers_unless_asked()
    {
        var (tree, hills) = Make();

        // A flower's column, from above
        int fx = 0, fz = 0, fy = 0;
        for (int x = 0; x < 200 && fy == 0; x++)
        for (int z = 0; z < 150 && fy == 0; z++)
        for (int y = 36; y < 60; y++)
        {
            if (hills.At(x, y, z) != Flower) continue;
            (fx, fy, fz) = (x, y, z);
            break;
        }

        Assert.NotEqual(0, fy);
        var origin = new Vector3(fx + 0.5f, 120.0f, fz + 0.5f);

        Assert.True(tree.Raycast(origin, -Vector3.UnitY, 200, foliage: false, liquid: false, out VoxelHit ground));
        Assert.Equal(fy - 1, ground.Y);
        Assert.Equal(Vector3.UnitY, ground.Normal);
        Assert.Equal((fx, fy, fz), ground.Adjacent);

        Assert.True(tree.Raycast(origin, -Vector3.UnitY, 200, foliage: true, liquid: false, out VoxelHit flower));
        Assert.Equal(fy, flower.Y);
        Assert.Equal(Flower, flower.Material);
    }

    [Fact]
    public void Somebody_dropped_on_the_hills_lands_on_them_and_walks_into_a_wall()
    {
        var (tree, _) = Make();
        var world = new CollisionWorld3D();
        world.Add(tree);

        int surface = tree.Surface(80, 80, countLiquid: false);
        var player = new CharacterController3D(world, new Vector3(80.5f, surface + 10, 80.5f));

        for (int i = 0; i < 240; i++) player.UpdatePhysics(1.0f / 120.0f);

        Assert.True(player.IsGrounded);
        Assert.Equal(surface + 1, player.Position.Y, 2);

        // A wall well over their head in front of them (the ground is bumpy by a block or two), which they walk into and stop at
        var wall = new List<VoxelEdit>();
        for (int y = surface - 3; y < surface + 8; y++)
        for (int z = 70; z < 90; z++)
            wall.Add(new VoxelEdit(83, y, z, Stone));
        tree.SetMany(wall.ToArray());

        player.Move = Vector3.UnitX;
        for (int i = 0; i < 240; i++) player.UpdatePhysics(1.0f / 120.0f);

        Assert.True(player.Position.X < 83.0f - player.Size.X * 0.5f + 0.01f, $"{player.Position.X}");
        Assert.True(player.Position.X > 82.0f, $"{player.Position.X}");
        Assert.True(player.HitWall);
    }

    [Fact]
    public void A_world_made_a_column_at_a_time_on_many_threads_is_the_same_world()
    {
        var hills = new Hills();
        var tree = VoxelTree.Empty(256, 128, 192, Palette, 4096, 4096);
        using (var streamer = new VoxelStreamer(tree, hills, threads: 4))
        {
            streamer.Focus = new Vector3(200, 0, 40);
            streamer.MakeAround(streamer.Focus, 1);
            Assert.True(streamer.IsReady(200, 40));
            Assert.False(streamer.IsComplete);

            streamer.Start();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!streamer.IsComplete && waited.Elapsed.TotalSeconds < 30) Thread.Sleep(5);
            Assert.True(streamer.IsComplete);
        }

        for (int y = 0; y < tree.SizeY; y++)
        for (int z = 0; z < tree.SizeZ; z += 3)
        for (int x = 0; x < tree.SizeX; x += 5)
            Assert.Equal(hills.At(x, y, z), tree.Get(x, y, z));

        // And it compacts down to the same thing a world made all at once is
        tree.Compact();
        for (int y = 0; y < tree.SizeY; y += 3)
        for (int z = 0; z < tree.SizeZ; z += 7)
        for (int x = 0; x < tree.SizeX; x += 2)
            Assert.Equal(hills.At(x, y, z), tree.Get(x, y, z));
    }

    [Fact]
    public void A_world_saved_half_made_carries_on_where_it_was()
    {
        var hills = new Hills();
        var tree = VoxelTree.Empty(256, 128, 192, Palette);
        bool[] done;
        using (var first = new VoxelStreamer(tree, hills, threads: 1))
        {
            first.MakeAround(new Vector3(32, 0, 32), 1);
            done = first.Done;
        }

        Assert.Contains(false, done);
        using var stream = new MemoryStream();
        tree.Save(stream, compact: false);
        stream.Position = 0;
        VoxelTree loaded = VoxelTree.Load(stream, Palette);

        using (var second = new VoxelStreamer(loaded, hills, threads: 2, alreadyDone: done))
        {
            Assert.Equal(done.Count(d => d), second.ColumnsDone);
            second.Start();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!second.IsComplete && waited.Elapsed.TotalSeconds < 30) Thread.Sleep(5);
            Assert.True(second.IsComplete);
        }

        for (int y = 0; y < loaded.SizeY; y += 2)
        for (int z = 0; z < loaded.SizeZ; z += 5)
        for (int x = 0; x < loaded.SizeX; x += 3)
            Assert.Equal(hills.At(x, y, z), loaded.Get(x, y, z));
    }

    [Fact]
    public void A_slab_is_stepped_up_and_a_block_needs_a_jump()
    {
        var floor = new BoxCollider3D(new Box3(new Vector3(-50, -1, -50), new Vector3(50, 0, 50)));
        var slab = new BoxCollider3D(new Box3(new Vector3(2, 0, -5), new Vector3(4, 0.5f, 5)));
        var world = new CollisionWorld3D();
        world.Add(floor);
        world.Add(slab);

        var player = new CharacterController3D(world, new Vector3(0, 0, 0)) { Move = Vector3.UnitX };
        for (int i = 0; i < 120; i++) player.UpdatePhysics(1.0f / 120.0f);

        Assert.True(player.Position.X > 2.5f, $"{player.Position}");
        Assert.Equal(0.5f, player.Position.Y, 2);
    }
}
