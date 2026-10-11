using System.Diagnostics;
using System.Numerics;

using AutoVoxel.Generation;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Logging;
using Horizon.Rendering.Voxels;

using static AutoVoxel.Blocks.Blocks;

namespace AutoVoxel.Gameplay;

/// <summary>
/// A tour of the world, for looking at it and measuring it without anybody at the keys (<c>--tour folder</c>). It
/// finds a few places worth seeing (a meadow, the whole world from a corner of the sky, a forest, the mountains, a
/// coast at sunset, under the sea, a cave, a cabin with its lamps lit at night), stands at each one long enough for the
/// light to settle, takes a screenshot into the folder, logs how long the frames took there, and leaves.
/// </summary>
public sealed class Tour(Player player, VoxelRenderer renderer, VoxelTree world, WorldGenerator generator, string folder) : GameComponent
{
    /// <summary>Whether the world is all there, the tour waits for it (it's made while the game runs).</summary>
    public Func<bool>? Ready { get; set; }

    private readonly Stopwatch waited = Stopwatch.StartNew();

    // How long it stands at a view before the picture is taken, and after
    private const float SETTLE = 3.5f, AFTER = 0.6f;

    // A view can move, along at Velocity blocks a second and turning at Turn radians a second, for seeing what the
    // things that lay frames over each other (the TAA, the bounce light) do with a camera that doesn't stand still
    private readonly record struct View(string Name, Vector3 Eye, Vector3 Target, float Time, Vector3 Velocity = default, float Turn = 0.0f, float Swing = 0.0f);

    // A swinging view goes left and right this often, and is looked at at the same point of every swing and then standing still
    private const float SWING_PERIOD = 2.0f;
    private const float SWING_FOR = 8.5f;

    private readonly List<View> views = [];
    private int building;
    private int index = -1;
    private float clock;
    private bool shot;

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        if (index < 0)
        {
            if (Ready is { } ready && !ready()) return;
            Log.Info($"[Tour] The world was all made {waited.Elapsed.TotalSeconds:0.00} s after the game started.");
            Plan();

            // Just some of them, and the renderer in a debug view, for measuring one thing at a time
            if (Environment.GetEnvironmentVariable("AUTOVOXEL_TOUR_VIEWS") is { Length: > 0 } only)
                views.RemoveAll(v => !only.Split(',').Any(name => v.Name.StartsWith(name.Trim())));
            if (int.TryParse(Environment.GetEnvironmentVariable("AUTOVOXEL_DEBUG_VIEW"), out int debugView)) renderer.DebugView = debugView;

            Directory.CreateDirectory(folder);
            player.Controlling = false;
            player.Body.Flying = true;
            player.Body.NoClip = true;
            Next();
        }

        if (index >= views.Count) return;

        clock += dt;
        View view = views[index];
        Look(view);

        // A swinging view, pictures at the same point of three swings, and once it has stood still for a while
        if (view.Swing != 0.0f)
        {
            // At the end of a swing, where it stands still for a moment, the same place every time
            foreach (float at in (ReadOnlySpan<float>)[4.5f, 6.5f, 8.5f, 11.5f])
            {
                if (clock >= at && clock - dt < at)
                    GameEngine.Instance.CaptureScreenshot(Path.Combine(folder, $"{index:00}_{view.Name}_{at:0}s.png"));
            }

            if (clock >= 12.0f) Next();
            return;
        }

        // A moving view gets a picture halfway through as well, mid move
        if (!shot && (view.Velocity != Vector3.Zero || view.Turn != 0.0f) && clock >= SETTLE * 0.5f && clock - dt < SETTLE * 0.5f)
            GameEngine.Instance.CaptureScreenshot(Path.Combine(folder, $"{index:00}_{view.Name}_moving.png"));

        if (!shot && clock >= SETTLE)
        {
            GameEngine.Instance.CaptureScreenshot(Path.Combine(folder, $"{index:00}_{view.Name}.png"));
            shot = true;
        }

        // The last one waits a bit longer, so its screenshot is written before the game goes
        // Building something, the way a player does it, through the player, a block every few frames once it has settled
        if (view.Name == "building" && clock > 1.0f && clock < SETTLE - 0.5f && ++building % 6 == 0) Build();

        if (clock >= SETTLE + AFTER * (index == views.Count - 1 ? 4.0f : 1.0f)) Next();
    }

    private void Next()
    {
        index++;
        clock = 0.0f;
        shot = false;

        if (index >= views.Count)
        {
            // And saved, for seeing what that costs and whether it comes back
            var clock = Stopwatch.StartNew();
            Gameplay.WorldFiles.Save(world, new WorldInfo
            {
                Name = "tour",
                Seed = generator.Seed,
                Size = world.SizeX,
                PlayerX = player.Body.Position.X,
                PlayerY = player.Body.Position.Y,
                PlayerZ = player.Body.Position.Z,
                Yaw = player.Yaw,
                Pitch = player.Pitch,
                TimeOfDay = renderer.TimeOfDay,
                Flying = true
            });
            Log.Info($"[Tour] Saved the world in {clock.Elapsed.TotalSeconds:0.00} s. That's the lot.");
            GameEngine.Instance.Exit();
            return;
        }

        View view = views[index];
        renderer.TimeOfDay = view.Time;
        Log.Info($"[Tour] {index:00} {view.Name} at {view.Eye.X:0} {view.Eye.Y:0} {view.Eye.Z:0}, {view.Time:0.0} o'clock, {Vector3.Distance(view.Eye, view.Target):0} blocks to what it looks at.");
    }

    private void Look(View view)
    {
        Vector3 eye = view.Eye + view.Velocity * clock;
        Vector3 look = Vector3.Normalize(view.Target + view.Velocity * clock - eye);
        float swing = view.Swing == 0.0f ? 0.0f : clock < SWING_FOR ? view.Swing * MathF.Sin(clock / SWING_PERIOD * MathF.Tau) : view.Swing;
        player.Yaw = MathF.Atan2(look.X, -look.Z) + view.Turn * clock + swing;
        player.Pitch = MathF.Asin(Math.Clamp(look.Y, -1.0f, 1.0f));
        player.Body.Teleport(eye - new Vector3(0.0f, player.Body.EyeHeight, 0.0f));
    }

    /// <summary>A step of the building view. Breaks into the ground for a while, then puts glowstone and bricks back on top.</summary>
    private void Build()
    {
        if (building < 24) player.Break();
        else
        {
            player.Select(building / 6 % 2 == 0 ? 7 : 8);
            player.Place();
        }
    }

    /* Where to go */

    private void Plan()
    {
        int size = world.SizeX;
        var (sx, sz) = generator.FindSpawn();
        int ground = Ground(sx, sz);

        var meadowEye = new Vector3(sx + 0.5f, ground + 2.7f, sz + 0.5f);
        views.Add(new View("meadow", meadowEye, meadowEye + new Vector3(30, -3, 12), 10.0f));
        views.Add(new View("swing", meadowEye + new Vector3(0, 1, 0), meadowEye + new Vector3(30, -2, 0), 11.0f, Swing: 0.9f));
        views.Add(new View("turning", meadowEye + new Vector3(0, 1, 0), meadowEye + new Vector3(30, -2, 0), 11.0f, Turn: 1.2f));
        views.Add(new View("flying", meadowEye + new Vector3(0, 60, 0), meadowEye + new Vector3(90, 20, 30), 13.0f, Velocity: new Vector3(18, 0, 6)));

        // The whole world from a corner of the sky, the render distance as far as it goes
        float corner = size * 0.06f;
        views.Add(new View("vista", new Vector3(corner, 236, corner), new Vector3(size * 0.6f, 60, size * 0.62f), 15.5f));

        if (Find(Biome.Forest, sx, sz) is { } forest)
        {
            int g = Ground(forest.X, forest.Z);
            var eye = new Vector3(forest.X + 0.5f, g + 1.8f, forest.Z + 0.5f);
            views.Add(new View("forest", eye, eye + new Vector3(10, 2.5f, 6), 12.5f));
        }

        if (Highest(sx, sz) is { } peak)
        {
            int g = Ground(peak.X, peak.Z);
            var target = new Vector3(peak.X, g - 25, peak.Z);
            views.Add(new View("mountains", target + new Vector3(-150, 55, -110), target, 8.5f));
        }

        if (Coast(sx, sz) is { } coast)
        {
            var eye = new Vector3(coast.X + 0.5f, Ground(coast.X, coast.Z) + 2.8f, coast.Z + 0.5f);
            views.Add(new View("sunset", eye, eye + new Vector3(-60, 6, 8), 17.85f));

            var under = new Vector3(coast.X - 40.5f, WorldGenerator.SEA_LEVEL - 4.0f, coast.Z + 0.5f);
            views.Add(new View("underwater", under, under + new Vector3(-20, -3, 6), 12.0f));
        }

        if (Cave(sx, sz) is { } cave) views.Add(new View("cave", cave.Eye, cave.Target, 12.0f));

        // Breaking a hole in the meadow and stacking a pillar, through the player like the mouse does it
        var digAt = new Vector3(sx + 0.5f, Ground(sx, sz) + 3.6f, sz - 3.5f);
        views.Add(new View("building", digAt, digAt + new Vector3(0.0f, -3.0f, 2.2f), 14.0f));

        // A cabin by the spawn with its lamps lit, from outside at night
        Vector3 cabin = BuildCabin(sx + 8, sz + 6);
        views.Add(new View("night", cabin + new Vector3(-9.5f, 3.5f, -12.5f), cabin + new Vector3(2.5f, 1.5f, 2.5f), 22.5f));
        views.Add(new View("dusk vista", new Vector3(corner, 200, size - corner), new Vector3(size * 0.6f, 60, size * 0.4f), 18.4f));
    }

    /// <summary>The top of the ground of a column, under any tree that's on it.</summary>
    private int Ground(int x, int z)
    {
        for (int y = world.SizeY - 1; y > 0; y--)
        {
            byte block = world.Get(x, y, z);
            if (block == Air || world.KindOf(block) is VoxelKind.Foliage or VoxelKind.Leaves or VoxelKind.Liquid) continue;
            if (block is OakLog or BirchLog or SpruceLog) continue;
            return Math.Max(y, WorldGenerator.SEA_LEVEL);
        }

        return WorldGenerator.SEA_LEVEL;
    }

    /// <summary>The nearest column of a biome, looking outwards from a point.</summary>
    private (int X, int Z)? Find(Biome biome, int x0, int z0)
    {
        for (int ring = 16; ring < world.SizeX / 2; ring += 16)
        {
            for (int a = 0; a < 48; a++)
            {
                float angle = a / 48.0f * MathF.Tau;
                int x = x0 + (int)(MathF.Cos(angle) * ring), z = z0 + (int)(MathF.Sin(angle) * ring);
                if (x < 8 || z < 8 || x >= world.SizeX - 8 || z >= world.SizeZ - 8) continue;
                if (generator.ColumnAt(x, z).Biome == biome) return (x, z);
            }
        }

        return null;
    }

    private (int X, int Z)? Highest(int x0, int z0)
    {
        (int X, int Z)? best = null;
        int highest = 0;
        for (int z = 32; z < world.SizeZ - 32; z += 24)
        for (int x = 32; x < world.SizeX - 32; x += 24)
        {
            int h = generator.ColumnAt(x, z).Height;
            if (h <= highest) continue;
            highest = h;
            best = (x, z);
        }

        return best;
    }

    /// <summary>A bit of beach with the sea off to the west of it, for the sun to go down over.</summary>
    private (int X, int Z)? Coast(int x0, int z0)
    {
        for (int ring = 16; ring < world.SizeX / 2; ring += 8)
        {
            for (int a = 0; a < 64; a++)
            {
                float angle = a / 64.0f * MathF.Tau;
                int x = x0 + (int)(MathF.Cos(angle) * ring), z = z0 + (int)(MathF.Sin(angle) * ring);
                if (x < 80 || z < 8 || x >= world.SizeX - 8 || z >= world.SizeZ - 8) continue;

                var here = generator.ColumnAt(x, z);
                if (here.Height < WorldGenerator.SEA_LEVEL || here.Height > WorldGenerator.SEA_LEVEL + 3) continue;

                // Open sky over it, no tree in the way
                int ground = Ground(x, z);
                bool open = true;
                for (int y = ground + 1; y < ground + 16 && open; y++) open = world.Get(x, y, z) == Air && world.Get(x - 1, y, z) == Air && world.Get(x, y, z - 1) == Air;
                if (!open) continue;
                if (generator.ColumnAt(x - 40, z).Height < WorldGenerator.SEA_LEVEL - 6 && generator.ColumnAt(x - 70, z).Height < WorldGenerator.SEA_LEVEL - 6)
                    return (x, z);
            }
        }

        return null;
    }

    /// <summary>Somewhere underground with room to stand, the lava lit ones first.</summary>
    private (Vector3 Eye, Vector3 Target)? Cave(int x0, int z0)
    {
        (Vector3, Vector3)? any = null;
        for (int ring = 0; ring < 400; ring += 6)
        {
            for (int a = 0; a < 24; a++)
            {
                float angle = a / 24.0f * MathF.Tau;
                int x = x0 + (int)(MathF.Cos(angle) * ring), z = z0 + (int)(MathF.Sin(angle) * ring);
                if (x < 16 || z < 16 || x >= world.SizeX - 16 || z >= world.SizeZ - 16) continue;

                int top = Ground(x, z) - 12;
                for (int y = 8; y < top; y++)
                {
                    if (!world.IsSolid(x, y - 1, z) || world.Get(x, y, z) != Air || world.Get(x, y + 1, z) != Air || world.Get(x, y + 2, z) != Air) continue;

                    // Some way to look along the cave
                    for (int d = 0; d < 4; d++)
                    {
                        int dx = d switch { 0 => 1, 1 => -1, _ => 0 }, dz = d switch { 2 => 1, 3 => -1, _ => 0 };
                        if (world.Get(x + dx * 6, y + 1, z + dz * 6) != Air) continue;

                        var eye = new Vector3(x + 0.5f, y + 1.6f, z + 0.5f);
                        var view = (eye, eye + new Vector3(dx * 10, -0.5f, dz * 10));
                        bool lava = false;
                        for (int k = -12; k <= 12 && !lava; k += 3)
                        for (int j = -12; j <= 12 && !lava; j += 3)
                            lava = world.Get(x + k, Math.Max(y - 2, 1), z + j) == Lava || world.Get(x + k, Math.Max(y - 3, 1), z + j) == Lava;

                        if (lava) return view;
                        any ??= view;
                    }
                }
            }

            if (any is not null && ring > 120) return any;
        }

        return any;
    }

    /// <summary>A little cabin with glass windows and lamps in and outside, its corner at a point. Says where its middle is.</summary>
    private Vector3 BuildCabin(int x0, int z0)
    {
        int floor = Ground(x0 + 2, z0 + 2) + 1;
        var edits = new List<VoxelEdit>();

        for (int y = 0; y <= 5; y++)
        for (int z = -1; z <= 6; z++)
        for (int x = -1; x <= 6; x++)
        {
            bool wall = x == 0 || x == 5 || z == 0 || z == 5;
            bool inside = x > 0 && x < 5 && z > 0 && z < 5;
            bool corner = (x == 0 || x == 5) && (z == 0 || z == 5);
            byte block = Air;

            if (y == 0 && x >= 0 && x <= 5 && z >= 0 && z <= 5) block = Planks;
            else if (y == 5 && x >= -1 && z >= -1) block = Bricks;
            else if (y > 0 && y < 5 && x >= 0 && x <= 5 && z >= 0 && z <= 5)
            {
                if (corner) block = OakLog;
                else if (wall) block = (y == 2 || y == 3) && (x == 2 || x == 3 || z == 2 || z == 3) ? Glass : Cobblestone;
                if (z == 0 && x == 2 && (y == 1 || y == 2)) block = Air;
                if (inside) block = Air;
            }

            if (y >= 0) edits.Add(new VoxelEdit(x0 + x, floor + y, z0 + z, block));
        }

        // Lamps, one in a corner inside, glowstone in the ceiling, lanterns either side of the door, and a few round the garden
        edits.Add(new VoxelEdit(x0 + 4, floor + 1, z0 + 4, Lantern));
        edits.Add(new VoxelEdit(x0 + 2, floor + 4, z0 + 3, Glowstone));
        edits.Add(new VoxelEdit(x0 + 1, floor + 3, z0 - 1, Lantern));
        edits.Add(new VoxelEdit(x0 + 3, floor + 3, z0 - 1, Lantern));
        for (int i = 0; i < 4; i++)
        {
            int lx = x0 - 6 + i * 5, lz = z0 - 7;
            int lg = Ground(lx, lz);
            edits.Add(new VoxelEdit(lx, lg + 1, lz, Cobblestone));
            edits.Add(new VoxelEdit(lx, lg + 2, lz, Lantern));
        }

        world.SetMany(edits.ToArray());
        return new Vector3(x0, floor, z0);
    }
}
