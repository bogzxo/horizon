using Horizon.Rendering.Voxels;

using static AutoVoxel.Blocks.Blocks;

namespace AutoVoxel.Generation;

/// <summary>What a column of the world is like, which decides what its ground is made of and what grows on it.</summary>
public enum Biome : byte
{
    Ocean,
    Beach,
    Desert,
    Savanna,
    Plains,
    Forest,
    BirchForest,
    Taiga,
    Snowy,
    Mountains,
    Peaks
}

/// <summary>
/// Makes the world, a continent in an ocean, out of a seed. Every voxel comes from where it is and the seed and
/// nothing else, so any chunk can be made on any thread in any order (see <see cref="IVoxelGenerator"/>), and a tree
/// that leans over into the next chunk is worked out again by that chunk, exactly the same way.
/// <para>
/// The ground. A continent shape at a very low frequency decides land and sea, and the world sinks into the ocean
/// at its edges, so the end of the world is a coast and not a cliff. Ridged noise is mountain ranges, kept to where
/// the land is high and well inland, detail noise on everything, rivers carved where a band of noise crosses zero,
/// and the whole lot pushed about by a third noise (domain warping) so nothing runs in straight lines.
/// Temperature and rain pick the biome, colder the higher up. Caves are tunnels where two 3D noises are both near
/// zero and caverns where a third is high, worked out every four voxels and blended in between, which is a
/// sixty fourth of the cost.
/// </para>
/// </summary>
public sealed class WorldGenerator : IVoxelGenerator
{
    public const int SEA_LEVEL = 62;
    private const int CHUNK = VoxelTree.CHUNK;

    // How far a tree can reach from its trunk, sideways and up
    private const int TREE_REACH = 5;
    private const int TREE_TOP = 24;

    // Trees grow one to a cell of this many blocks, somewhere in it
    private const int TREE_CELL = 6;

    private readonly Noise continent, mountains, mask, detail, warp, river, temperature, humidity, caveA, caveB, caverns, flowers, rocks;

    public int Seed { get; }
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    public WorldGenerator(int seed, int sizeX, int sizeY, int sizeZ)
    {
        Seed = seed;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;

        continent = new Noise(seed);
        mountains = new Noise(seed + 1);
        mask = new Noise(seed + 2);
        detail = new Noise(seed + 3);
        warp = new Noise(seed + 4);
        river = new Noise(seed + 5);
        temperature = new Noise(seed + 6);
        humidity = new Noise(seed + 7);
        caveA = new Noise(seed + 8);
        caveB = new Noise(seed + 9);
        caverns = new Noise(seed + 10);
        flowers = new Noise(seed + 11);
        rocks = new Noise(seed + 12);
    }

    /* Columns */

    public struct Column
    {
        public int Height;
        public Biome Biome;
        public byte Top, Filler;
        public int FillerDepth;
    }

    /// <summary>How high the ground is at a point, and what it's like there.</summary>
    public Column ColumnAt(int x, int z)
    {
        // Pushed about, so no ridge or river runs dead straight
        float wx = x + warp.Fractal(x * 0.0021f, z * 0.0021f, 3) * 90.0f;
        float wz = z + warp.Fractal(x * 0.0021f + 71.3f, z * 0.0021f - 13.1f, 3) * 90.0f;

        float land = continent.Fractal(wx * 0.00075f, wz * 0.00075f, 4) * 0.85f + 0.12f;

        // The world sinks into the sea at its edges
        float edge = MathF.Min(MathF.Min(x, SizeX - x), MathF.Min(z, SizeZ - z)) / (MathF.Min(SizeX, SizeZ) * 0.14f);
        if (edge < 1.0f) land = Lerp(-0.85f, land, Smooth(edge));

        float height = Spline(land);

        // Mountains where the land is well up and the mask says so
        float rangeMask = Smooth(Math.Clamp((land - 0.08f) * 2.6f + mask.Fractal(wx * 0.0012f, wz * 0.0012f, 2) * 0.9f, 0.0f, 1.0f));
        float ridge = mountains.Ridged(wx * 0.0028f, wz * 0.0028f, 5);
        height += rangeMask * MathF.Pow(ridge, 1.7f) * 150.0f;

        // Hills and bumps everywhere, more of them in the mountains
        height += detail.Fractal(x * 0.011f, z * 0.011f, 4) * (5.0f + 12.0f * rangeMask);

        // Rivers, a valley where the river noise crosses zero, down to just under the sea
        float r = MathF.Abs(river.Fractal(wx * 0.0013f, wz * 0.0013f, 3));
        if (land > -0.12f && height > SEA_LEVEL - 4)
        {
            float valley = Smooth(Math.Clamp(1.0f - r / 0.045f, 0.0f, 1.0f)) * (1.0f - rangeMask * 0.85f);
            height = Lerp(height, SEA_LEVEL - 3 - 3 * valley, valley * 0.92f);
        }

        int h = Math.Clamp((int)height, 3, SizeY - TREE_TOP - 4);

        // Colder going up, and wetter by its own noise
        float temp = temperature.Fractal(x * 0.0009f, z * 0.0009f, 3) * 1.1f - Math.Max(h - SEA_LEVEL, 0) / 160.0f;
        float wet = humidity.Fractal(x * 0.0011f + 50.0f, z * 0.0011f - 20.0f, 3) * 1.1f;

        var column = new Column { Height = h };
        column.Biome = PickBiome(h, temp, wet, rangeMask, r);
        Dress(ref column, x, z);
        return column;
    }

    private Biome PickBiome(int h, float temp, float wet, float rangeMask, float riverness)
    {
        if (h < SEA_LEVEL - 1) return Biome.Ocean;
        if (h > 168) return Biome.Peaks;
        if (h > 128 && rangeMask > 0.45f) return Biome.Mountains;
        if (h <= SEA_LEVEL + 2 && temp > -0.3f && riverness > 0.03f) return Biome.Beach;
        if (temp > 0.32f) return wet < -0.05f ? Biome.Desert : Biome.Savanna;
        if (temp < -0.38f) return Biome.Snowy;
        if (temp < -0.12f) return Biome.Taiga;
        if (wet > 0.42f) return Biome.BirchForest;
        if (wet > 0.12f) return Biome.Forest;
        return Biome.Plains;
    }

    /// <summary>What the top of a column is and what is under it, by its biome.</summary>
    private void Dress(ref Column column, int x, int z)
    {
        column.FillerDepth = 3 + (int)(MathF.Abs(rocks.Sample(x * 0.05f, z * 0.05f)) * 3.0f);
        switch (column.Biome)
        {
            case Biome.Ocean:
                float floor = rocks.Sample(x * 0.03f, z * 0.03f);
                column.Top = floor > 0.35f ? Gravel : floor < -0.4f ? Clay : Sand;
                column.Filler = column.Top == Clay ? Clay : Sand;
                break;
            case Biome.Beach:
            case Biome.Desert:
                column.Top = Sand;
                column.Filler = Sand;
                column.FillerDepth += 2;
                break;
            case Biome.Savanna:
                column.Top = DryGrass;
                column.Filler = Dirt;
                break;
            case Biome.Snowy:
                column.Top = SnowyGrass;
                column.Filler = Dirt;
                break;
            case Biome.Taiga:
                column.Top = rocks.Sample(x * 0.04f, z * 0.04f) > 0.3f ? Podzol : Grass;
                column.Filler = Dirt;
                break;
            case Biome.Mountains:
                float stone = rocks.Sample(x * 0.06f, z * 0.06f);
                column.Top = stone > 0.1f ? Stone : stone > -0.2f ? Gravel : Grass;
                column.Filler = column.Top == Grass ? Dirt : Stone;
                break;
            case Biome.Peaks:
                column.Top = column.Height > 178 ? Snow : Stone;
                column.Filler = Stone;
                column.FillerDepth = 1;
                break;
            default:
                column.Top = Grass;
                column.Filler = Dirt;
                break;
        }
    }

    // Land from the continent number, a curve through a few points, so the coast is a shelf and the inland rises gently
    private static float Spline(float c)
    {
        ReadOnlySpan<float> xs = [-1.0f, -0.45f, -0.16f, -0.05f, 0.04f, 0.25f, 0.55f, 1.0f];
        ReadOnlySpan<float> ys = [18.0f, 38.0f, 54.0f, 61.0f, 65.0f, 74.0f, 88.0f, 104.0f];
        if (c <= xs[0]) return ys[0];
        for (int i = 1; i < xs.Length; i++)
        {
            if (c > xs[i]) continue;
            float t = (c - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return Lerp(ys[i - 1], ys[i], Smooth(t));
        }

        return ys[^1];
    }

    /* Chunks */

    [ThreadStatic] private static Column[]? columns;
    [ThreadStatic] private static (int X, int Z) columnsOf;
    [ThreadStatic] private static float[]? caves;

    public bool Generate(int cx, int cy, int cz, Span<byte> voxels)
    {
        int x0 = cx * CHUNK, y0 = cy * CHUNK, z0 = cz * CHUNK;
        Column[] cols = Columns(cx, cz);

        int highest = 0, lowest = int.MaxValue;
        foreach (Column c in cols)
        {
            highest = Math.Max(highest, c.Height);
            lowest = Math.Min(lowest, c.Height);
        }

        // Nothing reaches up here, not even a tree
        if (y0 > Math.Max(highest, SEA_LEVEL) + TREE_TOP) return false;

        bool any = false;
        bool hasCaves = y0 < highest - 2;
        float[]? caveGrid = hasCaves ? Caves(x0, y0, z0) : null;

        for (int lz = 0; lz < CHUNK; lz++)
        {
            for (int lx = 0; lx < CHUNK; lx++)
            {
                Column c = cols[lx + lz * CHUNK];
                int x = x0 + lx, z = z0 + lz;
                bool openToSky = c.Height > SEA_LEVEL + 1;

                for (int ly = 0; ly < CHUNK; ly++)
                {
                    int y = y0 + ly;
                    byte block;

                    if (y > c.Height)
                    {
                        if (y > SEA_LEVEL) break;
                        block = y == SEA_LEVEL && c.Biome == Biome.Snowy ? Ice : Water;
                    }
                    else
                    {
                        int depth = c.Height - y;
                        if (y == 0) block = Bedrock;
                        else if (y < 3 && Hash(x, y, z) < 0.5f) block = Bedrock;
                        else if (depth == 0) block = c.Top;
                        else if (depth <= c.FillerDepth) block = c.Filler == Sand && depth > 2 ? Sandstone : c.Filler;
                        else block = y < 20 + ((int)(Hash(x >> 2, 7, z >> 2) * 2) << 2) ? Deepslate : Stone;

                        // Caves, kept off the sea floor so the ocean doesn't drain into them
                        if (caveGrid is not null && y > 2 && (openToSky || depth > 6) && Cave(caveGrid, lx, ly, lz, y, depth))
                            block = y < 11 ? Lava : Air;
                        else if (block == Stone || block == Deepslate)
                            block = Ore(x, y, z, block);
                    }

                    if (block == Air) continue;
                    voxels[lx + lz * CHUNK + ly * CHUNK * CHUNK] = block;
                    any = true;
                }
            }
        }

        any |= PlantFoliage(cols, x0, y0, z0, voxels);
        any |= GrowTrees(x0, y0, z0, voxels, cols);
        return any;
    }

    private Column[] Columns(int cx, int cz)
    {
        columns ??= new Column[CHUNK * CHUNK];
        if (columnsOf == (cx + 1, cz + 1)) return columns;

        for (int lz = 0; lz < CHUNK; lz++)
        for (int lx = 0; lx < CHUNK; lx++)
            columns[lx + lz * CHUNK] = ColumnAt(cx * CHUNK + lx, cz * CHUNK + lz);

        // Off by one so the first chunk, (0, 0), isn't taken for one that's cached already
        columnsOf = (cx + 1, cz + 1);
        return columns;
    }

    /* Caves */

    private const int CAVE_STEP = 4;
    private const int CAVE_GRID = CHUNK / CAVE_STEP + 1;

    /// <summary>Helper method to work out the caves every four voxels of a chunk, for blending in between.</summary>
    private float[] Caves(int x0, int y0, int z0)
    {
        caves ??= new float[CAVE_GRID * CAVE_GRID * CAVE_GRID];
        for (int gy = 0; gy < CAVE_GRID; gy++)
        for (int gz = 0; gz < CAVE_GRID; gz++)
        for (int gx = 0; gx < CAVE_GRID; gx++)
        {
            float x = x0 + gx * CAVE_STEP, y = y0 + gy * CAVE_STEP, z = z0 + gz * CAVE_STEP;

            // Tunnels where two noises are both near nothing, a line through space, and caverns where a third is high
            float a = caveA.Sample(x * 0.011f, y * 0.016f, z * 0.011f);
            float b = caveB.Sample(x * 0.011f, y * 0.016f, z * 0.011f);
            float tunnel = a * a + b * b;
            float cavern = caverns.Sample(x * 0.0075f, y * 0.013f, z * 0.0075f);

            // Negative is air. Caverns only deep down, tunnels everywhere
            float depthFactor = Math.Clamp((70.0f - y) / 40.0f, 0.0f, 1.0f);
            caves[gx + gz * CAVE_GRID + gy * CAVE_GRID * CAVE_GRID] = MathF.Min(tunnel - 0.016f, (0.62f - cavern) * 0.1f / MathF.Max(depthFactor, 0.01f));
        }

        return caves;
    }

    private static bool Cave(float[] grid, int lx, int ly, int lz, int y, int depth)
    {
        int gx = lx / CAVE_STEP, gy = ly / CAVE_STEP, gz = lz / CAVE_STEP;
        float fx = (lx % CAVE_STEP) / (float)CAVE_STEP, fy = (ly % CAVE_STEP) / (float)CAVE_STEP, fz = (lz % CAVE_STEP) / (float)CAVE_STEP;

        float c000 = grid[Index(gx, gy, gz)], c100 = grid[Index(gx + 1, gy, gz)];
        float c010 = grid[Index(gx, gy + 1, gz)], c110 = grid[Index(gx + 1, gy + 1, gz)];
        float c001 = grid[Index(gx, gy, gz + 1)], c101 = grid[Index(gx + 1, gy, gz + 1)];
        float c011 = grid[Index(gx, gy + 1, gz + 1)], c111 = grid[Index(gx + 1, gy + 1, gz + 1)];

        float v = Lerp(
            Lerp(Lerp(c000, c100, fx), Lerp(c010, c110, fx), fy),
            Lerp(Lerp(c001, c101, fx), Lerp(c011, c111, fx), fy), fz);

        // Fewer openings right at the surface, a cave mouth now and then rather than a sponge
        if (depth < 3) v += 0.01f;
        return v < 0.0f;

        static int Index(int x, int y, int z) => x + z * CAVE_GRID + y * CAVE_GRID * CAVE_GRID;
    }

    private byte Ore(int x, int y, int z, byte stone)
    {
        // In veins, a hash of the grid of four the tree keeps its smallest nodes on picks the vein and a finer one fills it
        // in. On that grid on purpose, a node of four with no ore in it is solid stone all the way through and costs one
        // node, a node with a bit of ore in it costs every voxel of it. Ore sprinkled about on a finer grid was in one of
        // every seven of them and two thirds of the memory of an 8192 world
        float vein = Hash(x >> 2, y >> 2, z >> 2);
        if (Hash(x, y, z) < 0.45f) return stone;

        if (y < 16 && vein < 0.0025f) return DiamondOre;
        if (y < 32 && vein < 0.006f) return GoldOre;
        if (y < 64 && vein < 0.016f) return IronOre;
        if (y < 128 && vein < 0.028f) return CoalOre;
        if (vein > 0.996f && y < 90) return Granite;
        return stone;
    }

    /* What grows */

    private bool PlantFoliage(Column[] cols, int x0, int y0, int z0, Span<byte> voxels)
    {
        bool any = false;
        for (int lz = 0; lz < CHUNK; lz++)
        {
            for (int lx = 0; lx < CHUNK; lx++)
            {
                Column c = cols[lx + lz * CHUNK];
                int ly = c.Height + 1 - y0;
                if (ly < 0 || ly >= CHUNK || c.Height < SEA_LEVEL) continue;

                // Only on its own ground (a cave mouth took it away, or it's under the sea)
                if (ly > 0 && voxels[lx + lz * CHUNK + (ly - 1) * CHUNK * CHUNK] != c.Top) continue;
                if (ly == 0 && y0 > 0) continue;

                int x = x0 + lx, z = z0 + lz;
                byte plant = Plant(c, x, z);
                if (plant == Air) continue;

                ref byte at = ref voxels[lx + lz * CHUNK + ly * CHUNK * CHUNK];
                if (at != Air) continue;
                at = plant;
                any = true;
            }
        }

        return any;
    }

    private byte Plant(Column c, int x, int z)
    {
        float roll = Hash(x, 1031, z);
        float meadow = flowers.Sample(x * 0.02f, z * 0.02f);
        float kind = Hash(x, 2063, z);

        switch (c.Biome)
        {
            case Biome.Plains:
                if (meadow > 0.35f && roll < 0.45f) return Flower(x, z);
                if (roll < 0.42f) return kind < 0.8f ? GrassTuft : TallGrass;
                return Air;
            case Biome.Forest:
            case Biome.BirchForest:
                if (roll < 0.02f) return kind < 0.5f ? RedMushroom : BrownMushroom;
                if (meadow > 0.5f && roll < 0.25f) return Flower(x, z);
                if (roll < 0.3f) return kind < 0.7f ? GrassTuft : Fern;
                return Air;
            case Biome.Taiga:
                if (roll < 0.015f) return BrownMushroom;
                if (roll < 0.3f) return kind < 0.55f ? Fern : GrassTuft;
                return Air;
            case Biome.Savanna:
                if (roll < 0.35f) return kind < 0.6f ? TallGrass : GrassTuft;
                return Air;
            case Biome.Desert:
                return roll < 0.012f ? DeadBush : Air;
            case Biome.Mountains:
                return c.Top == Grass && roll < 0.18f ? GrassTuft : Air;
            case Biome.Snowy:
                return roll < 0.03f ? Fern : Air;
            default:
                return Air;
        }
    }

    private byte Flower(int x, int z)
    {
        // Patches of one colour, which is how wild flowers grow
        float which = flowers.Sample(x * 0.008f + 300.0f, z * 0.008f);
        return which < -0.3f ? Poppy : which < 0.0f ? Dandelion : which < 0.3f ? Cornflower : Daisy;
    }

    private bool GrowTrees(int x0, int y0, int z0, Span<byte> voxels, Column[] cols)
    {
        bool any = false;
        int cellFrom = (x0 - TREE_REACH) / TREE_CELL - 1, cellTo = (x0 + CHUNK + TREE_REACH) / TREE_CELL + 1;
        int cellFromZ = (z0 - TREE_REACH) / TREE_CELL - 1, cellToZ = (z0 + CHUNK + TREE_REACH) / TREE_CELL + 1;

        for (int cz = cellFromZ; cz <= cellToZ; cz++)
        {
            for (int cx = cellFrom; cx <= cellTo; cx++)
            {
                if (cx < 0 || cz < 0) continue;

                // Somewhere in the cell, or nowhere
                int tx = cx * TREE_CELL + (int)(Hash(cx, 11, cz) * TREE_CELL);
                int tz = cz * TREE_CELL + (int)(Hash(cx, 13, cz) * TREE_CELL);
                if (tx < 2 || tz < 2 || tx >= SizeX - 2 || tz >= SizeZ - 2) continue;

                // Its column, from the chunk's own if it's in it
                Column column = tx >= x0 && tx < x0 + CHUNK && tz >= z0 && tz < z0 + CHUNK
                    ? cols[(tx - x0) + (tz - z0) * CHUNK]
                    : ColumnAt(tx, tz);

                if (column.Height <= SEA_LEVEL) continue;
                float chance = column.Biome switch
                {
                    Biome.Forest => 0.75f,
                    Biome.BirchForest => 0.7f,
                    Biome.Taiga => 0.6f,
                    Biome.Snowy => 0.12f,
                    Biome.Plains => 0.045f,
                    Biome.Savanna => 0.06f,
                    Biome.Mountains => column.Top == Grass ? 0.15f : 0.0f,
                    Biome.Desert => 0.1f,
                    _ => 0.0f
                };

                if (Hash(cx, 17, cz) >= chance) continue;

                int ground = column.Height;
                if (ground + TREE_TOP < y0 || ground - 2 >= y0 + CHUNK) continue;

                var writer = new TreeWriter(voxels, x0, y0, z0);
                float roll = Hash(cx, 19, cz);
                switch (column.Biome)
                {
                    case Biome.Desert:
                        GrowCactus(ref writer, tx, ground + 1, tz, 2 + (int)(roll * 3));
                        break;
                    case Biome.Taiga:
                    case Biome.Snowy:
                    case Biome.Mountains:
                        Spruce(ref writer, tx, ground + 1, tz, 8 + (int)(roll * 7), cx, cz);
                        break;
                    case Biome.BirchForest:
                        if (roll < 0.8f) Birch(ref writer, tx, ground + 1, tz, 6 + (int)(Hash(cx, 23, cz) * 3), cx, cz);
                        else Oak(ref writer, tx, ground + 1, tz, 5 + (int)(Hash(cx, 23, cz) * 2), cx, cz);
                        break;
                    case Biome.Savanna:
                        Acacia(ref writer, tx, ground + 1, tz, cx, cz);
                        break;
                    default:
                        if (roll < 0.12f) BigOak(ref writer, tx, ground + 1, tz, cx, cz);
                        else if (roll < 0.3f) Birch(ref writer, tx, ground + 1, tz, 6 + (int)(Hash(cx, 23, cz) * 3), cx, cz);
                        else Oak(ref writer, tx, ground + 1, tz, 5 + (int)(Hash(cx, 23, cz) * 2), cx, cz);
                        break;
                }

                any |= writer.Wrote;
            }
        }

        return any;
    }

    /// <summary>Writes a tree's voxels into a chunk, the ones that land in it, without trampling the trunk with leaves.</summary>
    private ref struct TreeWriter(Span<byte> voxels, int x0, int y0, int z0)
    {
        private readonly Span<byte> voxels = voxels;
        public bool Wrote;

        public void Log(int x, int y, int z, byte block) => Put(x, y, z, block, true);

        public void Leaf(int x, int y, int z, byte block) => Put(x, y, z, block, false);

        private void Put(int x, int y, int z, byte block, bool solid)
        {
            int lx = x - x0, ly = y - y0, lz = z - z0;
            if ((uint)lx >= CHUNK || (uint)ly >= CHUNK || (uint)lz >= CHUNK) return;

            ref byte at = ref voxels[lx + lz * CHUNK + ly * CHUNK * CHUNK];
            if (!solid && at != Air && at < GrassTuft) return;
            if (solid && at != Air && at < GrassTuft && at != OakLeaves && at != BirchLeaves && at != SpruceLeaves) return;
            at = block;
            Wrote = true;
        }
    }

    private void Oak(ref TreeWriter w, int x, int y, int z, int height, int cx, int cz)
    {
        for (int i = 0; i < height; i++) w.Log(x, y + i, z, OakLog);

        int top = y + height;
        for (int dy = -3; dy <= 1; dy++)
        {
            int radius = dy <= -2 ? 2 : 1;
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                bool corner = Math.Abs(dx) == radius && Math.Abs(dz) == radius;
                if (corner && (dy == 1 || Hash(x + dx * 7, y + dy, z + dz * 13 + cx) < 0.5f)) continue;
                w.Leaf(x + dx, top + dy, z + dz, OakLeaves);
            }
        }
    }

    private void BigOak(ref TreeWriter w, int x, int y, int z, int cx, int cz)
    {
        int height = 8 + (int)(Hash(cx, 29, cz) * 4);
        for (int i = 0; i < height; i++) w.Log(x, y + i, z, OakLog);

        // A few branches out to blobs of leaves, and one big blob at the top
        int top = y + height;
        Blob(ref w, x, top, z, 3.6f, OakLeaves, cx);
        for (int b = 0; b < 3; b++)
        {
            float angle = Hash(cx, 31 + b, cz) * MathF.Tau;
            int length = 2 + (int)(Hash(cx, 37 + b, cz) * 2);
            int by = y + height - 3 - b;
            int ex = x, ez = z;
            for (int k = 1; k <= length; k++)
            {
                ex = x + (int)MathF.Round(MathF.Cos(angle) * k);
                ez = z + (int)MathF.Round(MathF.Sin(angle) * k);
                w.Log(ex, by + k / 2, ez, OakLog);
            }

            Blob(ref w, ex, by + length / 2 + 1, ez, 2.4f, OakLeaves, cx + b);
        }
    }

    private void Birch(ref TreeWriter w, int x, int y, int z, int height, int cx, int cz)
    {
        for (int i = 0; i < height; i++) w.Log(x, y + i, z, BirchLog);

        int top = y + height;
        for (int dy = -3; dy <= 1; dy++)
        {
            int radius = dy <= -2 ? 2 : 1;
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                bool corner = Math.Abs(dx) == radius && Math.Abs(dz) == radius;
                if (corner) continue;
                w.Leaf(x + dx, top + dy, z + dz, BirchLeaves);
            }
        }
    }

    private void Spruce(ref TreeWriter w, int x, int y, int z, int height, int cx, int cz)
    {
        for (int i = 0; i < height; i++) w.Log(x, y + i, z, SpruceLog);

        // Rings of needles, wide at the bottom and narrowing up, every other ring a step in
        int top = y + height;
        w.Leaf(x, top, z, SpruceLeaves);
        w.Leaf(x, top + 1, z, SpruceLeaves);
        int start = y + 2 + (int)(Hash(cx, 41, cz) * 2);
        for (int ly = start; ly < top; ly++)
        {
            float along = (float)(top - ly) / (top - start);
            int radius = (int)MathF.Round(along * 3.2f) - ((top - ly) % 2 == 0 ? 1 : 0);
            radius = Math.Max(radius, ly == top - 1 ? 1 : 0);
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dz * dz > radius * radius + 1) continue;
                w.Leaf(x + dx, ly, z + dz, SpruceLeaves);
            }
        }
    }

    private void Acacia(ref TreeWriter w, int x, int y, int z, int cx, int cz)
    {
        int height = 5 + (int)(Hash(cx, 43, cz) * 2);
        int dx = Hash(cx, 47, cz) < 0.5f ? 1 : -1, dz = Hash(cx, 53, cz) < 0.5f ? 1 : -1;
        int tx = x, tz = z;
        for (int i = 0; i < height; i++)
        {
            if (i > 2 && i % 2 == 1)
            {
                tx += dx;
                tz += dz;
            }

            w.Log(tx, y + i, tz, OakLog);
        }

        // A flat umbrella of leaves
        int top = y + height;
        for (int ddz = -3; ddz <= 3; ddz++)
        for (int ddx = -3; ddx <= 3; ddx++)
        {
            if (ddx * ddx + ddz * ddz > 10) continue;
            w.Leaf(tx + ddx, top, tz + ddz, OakLeaves);
            if (ddx * ddx + ddz * ddz <= 3) w.Leaf(tx + ddx, top + 1, tz + ddz, OakLeaves);
        }
    }

    private static void GrowCactus(ref TreeWriter w, int x, int y, int z, int height)
    {
        for (int i = 0; i < height; i++) w.Log(x, y + i, z, Cactus);
    }

    private void Blob(ref TreeWriter w, int x, int y, int z, float radius, byte leaves, int salt)
    {
        int r = (int)MathF.Ceiling(radius);
        for (int dy = -r; dy <= r; dy++)
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            float d = MathF.Sqrt(dx * dx + dy * dy * 1.6f + dz * dz);
            if (d > radius + (Hash(x + dx, y + dy + salt, z + dz) - 0.5f) * 0.9f) continue;
            w.Leaf(x + dx, y + dy, z + dz, leaves);
        }
    }

    /* Bits */

    /// <summary>A number from 0 to 1 for a point and the seed, the same every time.</summary>
    private float Hash(int x, int y, int z)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2246822519u + (uint)Seed * 3266489917u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216.0f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>Where somebody starts, the middle of the world or the nearest bit of dry land to it that isn't a mountain.</summary>
    public (int X, int Z) FindSpawn()
    {
        int cx = SizeX / 2, cz = SizeZ / 2;
        for (int ring = 0; ring < Math.Min(SizeX, SizeZ) / 2; ring += 8)
        {
            for (int a = 0; a < 32; a++)
            {
                float angle = a / 32.0f * MathF.Tau;
                int x = cx + (int)(MathF.Cos(angle) * ring), z = cz + (int)(MathF.Sin(angle) * ring);
                Column c = ColumnAt(x, z);
                if (c.Height > SEA_LEVEL + 2 && c.Height < 110 && c.Biome is Biome.Plains or Biome.Forest or Biome.Savanna or Biome.BirchForest)
                    return (x, z);
            }
        }

        return (cx, cz);
    }
}
