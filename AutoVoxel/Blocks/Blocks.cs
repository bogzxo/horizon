using System.Numerics;

using Horizon.Rendering.Voxels;

namespace AutoVoxel.Blocks;

/// <summary>
/// Every block there is, what it's made of, which tiles of the atlas it wears and the little models the foliage is.
/// The numbers are what a voxel is, so they are what a save file is, add to the end and never move one. The tile
/// numbers are the order of TILES in Art/make_atlas.py.
/// </summary>
public static class Blocks
{
    public const byte Air = 0;
    public const byte Stone = 1;
    public const byte Dirt = 2;
    public const byte Grass = 3;
    public const byte Sand = 4;
    public const byte Gravel = 5;
    public const byte SnowyGrass = 6;
    public const byte Snow = 7;
    public const byte Water = 8;
    public const byte OakLog = 9;
    public const byte BirchLog = 10;
    public const byte SpruceLog = 11;
    public const byte OakLeaves = 12;
    public const byte BirchLeaves = 13;
    public const byte SpruceLeaves = 14;
    public const byte Planks = 15;
    public const byte Cobblestone = 16;
    public const byte Glass = 17;
    public const byte Sandstone = 18;
    public const byte Clay = 19;
    public const byte Ice = 20;
    public const byte Bricks = 21;
    public const byte Glowstone = 22;
    public const byte CoalOre = 23;
    public const byte IronOre = 24;
    public const byte GoldOre = 25;
    public const byte DiamondOre = 26;
    public const byte Lava = 27;
    public const byte MossyCobblestone = 28;
    public const byte Deepslate = 29;
    public const byte Granite = 30;
    public const byte Cactus = 31;
    public const byte RedSand = 32;
    public const byte Terracotta = 33;
    public const byte DryGrass = 34;
    public const byte Lantern = 35;
    public const byte Bedrock = 36;
    public const byte Podzol = 37;

    // The foliage, a little model inside one block each
    public const byte GrassTuft = 40;
    public const byte TallGrass = 41;
    public const byte Poppy = 42;
    public const byte Dandelion = 43;
    public const byte Cornflower = 44;
    public const byte Daisy = 45;
    public const byte Fern = 46;
    public const byte DeadBush = 47;
    public const byte RedMushroom = 48;
    public const byte BrownMushroom = 49;

    /// <summary>The tiles, by the order Art/make_atlas.py paints them in.</summary>
    private static class Tile
    {
        public const byte GrassTop = 0, GrassSide = 1, Dirt = 2, Stone = 3, Cobblestone = 4, Sand = 5, Gravel = 6, Snow = 7,
            SnowSide = 8, OakLog = 9, LogTop = 10, BirchLog = 11, SpruceLog = 12, Planks = 13, Water = 14, Glass = 15,
            OakLeaves = 16, BirchLeaves = 17, SpruceLeaves = 18, Sandstone = 19, SandstoneTop = 20, Clay = 21, Ice = 22,
            Bricks = 23, Glowstone = 24, CoalOre = 25, IronOre = 26, GoldOre = 27, DiamondOre = 28, Lava = 29,
            MossyCobblestone = 30, Deepslate = 31, Granite = 32, Cactus = 33, CactusTop = 34, RedSand = 35,
            Terracotta = 36, DryGrassTop = 37, DryGrassSide = 38, Lantern = 39, Bedrock = 40, Podzol = 41;
    }

    /// <summary>What the hotbar starts with, the blocks a builder wants first.</summary>
    public static readonly byte[] Hotbar = [Grass, Dirt, Stone, Cobblestone, Planks, OakLog, Glass, Bricks, Glowstone, Lantern, Sand, Water];

    /// <summary>Makes the palette, every block in its place and the models of the foliage.</summary>
    public static VoxelPalette CreatePalette()
    {
        var palette = new VoxelPalette { AtlasTilesPerRow = 16 };

        Put(palette, Stone, Cube("stone", Tile.Stone));
        Put(palette, Dirt, Cube("dirt", Tile.Dirt));
        Put(palette, Grass, Cube("grass", Tile.GrassTop, Tile.GrassSide, Tile.Dirt));
        Put(palette, Sand, Cube("sand", Tile.Sand));
        Put(palette, Gravel, Cube("gravel", Tile.Gravel));
        Put(palette, SnowyGrass, Cube("snowy grass", Tile.Snow, Tile.SnowSide, Tile.Dirt));
        Put(palette, Snow, Cube("snow", Tile.Snow));
        Put(palette, Water, new VoxelMaterial
        {
            Name = "water",
            Kind = VoxelKind.Liquid,
            Color = VoxelPalette.Srgb(40, 96, 150),
            TopTile = VoxelPalette.NO_TILE,
            SideTile = VoxelPalette.NO_TILE,
            BottomTile = VoxelPalette.NO_TILE,
            Roughness = 0.0f
        });
        Put(palette, OakLog, Cube("oak log", Tile.LogTop, Tile.OakLog, Tile.LogTop));
        Put(palette, BirchLog, Cube("birch log", Tile.LogTop, Tile.BirchLog, Tile.LogTop));
        Put(palette, SpruceLog, Cube("spruce log", Tile.LogTop, Tile.SpruceLog, Tile.LogTop));
        Put(palette, OakLeaves, Leaves("oak leaves", Tile.OakLeaves));
        Put(palette, BirchLeaves, Leaves("birch leaves", Tile.BirchLeaves));
        Put(palette, SpruceLeaves, Leaves("spruce leaves", Tile.SpruceLeaves));
        Put(palette, Planks, Cube("planks", Tile.Planks));
        Put(palette, Cobblestone, Cube("cobblestone", Tile.Cobblestone));
        Put(palette, Glass, new VoxelMaterial
        {
            Name = "glass",
            Kind = VoxelKind.Glass,
            Color = VoxelPalette.Srgb(210, 235, 245),
            TopTile = VoxelPalette.NO_TILE,
            SideTile = VoxelPalette.NO_TILE,
            BottomTile = VoxelPalette.NO_TILE,
            Roughness = 0.05f
        });
        Put(palette, Sandstone, Cube("sandstone", Tile.SandstoneTop, Tile.Sandstone, Tile.SandstoneTop));
        Put(palette, Clay, Cube("clay", Tile.Clay));
        Put(palette, Ice, Cube("ice", Tile.Ice) with { Roughness = 0.1f });
        Put(palette, Bricks, Cube("bricks", Tile.Bricks));
        Put(palette, Glowstone, Cube("glowstone", Tile.Glowstone) with { Emission = VoxelPalette.Srgb(255, 214, 140) * 3.5f });
        Put(palette, CoalOre, Cube("coal ore", Tile.CoalOre));
        Put(palette, IronOre, Cube("iron ore", Tile.IronOre));
        Put(palette, GoldOre, Cube("gold ore", Tile.GoldOre));
        Put(palette, DiamondOre, Cube("diamond ore", Tile.DiamondOre));
        Put(palette, Lava, Cube("lava", Tile.Lava) with { Emission = VoxelPalette.Srgb(255, 110, 30) * 1.3f });
        Put(palette, MossyCobblestone, Cube("mossy cobblestone", Tile.MossyCobblestone));
        Put(palette, Deepslate, Cube("deepslate", Tile.Deepslate));
        Put(palette, Granite, Cube("granite", Tile.Granite));
        Put(palette, Cactus, Cube("cactus", Tile.CactusTop, Tile.Cactus, Tile.CactusTop));
        Put(palette, RedSand, Cube("red sand", Tile.RedSand));
        Put(palette, Terracotta, Cube("terracotta", Tile.Terracotta));
        Put(palette, DryGrass, Cube("dry grass", Tile.DryGrassTop, Tile.DryGrassSide, Tile.Dirt));
        Put(palette, Lantern, Cube("lantern", Tile.Lantern) with { Emission = VoxelPalette.Srgb(255, 190, 110) * 4.5f });
        Put(palette, Bedrock, Cube("bedrock", Tile.Bedrock));
        Put(palette, Podzol, Cube("podzol", Tile.Podzol, Tile.GrassSide, Tile.Dirt));

        AddFoliage(palette);

        // The averages of the tiles, so a mountain looks the same from a mile off as it does up close
        palette.AverageTiles(Path.Combine(AppContext.BaseDirectory, "content", "atlas.png"));

        return palette;
    }

    /// <summary>Whether a block is something to stand on and walk into.</summary>
    public static bool IsSolid(VoxelPalette palette, byte block) => palette[block].IsSolid;

    public static string NameOf(VoxelPalette palette, byte block) => block < palette.Count ? palette[block].Name : "?";

    private static VoxelMaterial Cube(string name, byte tile) => Cube(name, tile, tile, tile);

    private static VoxelMaterial Cube(string name, byte top, byte side, byte bottom) => new()
    {
        Name = name,
        Kind = VoxelKind.Solid,
        Color = new Vector3(0.5f),
        TopTile = top,
        SideTile = side,
        BottomTile = bottom,
        Roughness = 1.0f
    };

    private static VoxelMaterial Leaves(string name, byte tile) => new()
    {
        Name = name,
        Kind = VoxelKind.Leaves,
        Color = new Vector3(0.1f, 0.25f, 0.06f),
        TopTile = tile,
        SideTile = tile,
        BottomTile = tile,
        Roughness = 1.0f,
        Sways = true
    };

    /// <summary>Puts a material at the number it has to have, with air in front of it where there are gaps.</summary>
    private static void Put(VoxelPalette palette, byte at, VoxelMaterial material)
    {
        while (palette.Count < at) palette.Add(new VoxelMaterial { Name = $"unused {palette.Count}", Kind = VoxelKind.Air });
        if (palette.Count != at) throw new InvalidOperationException($"Block {at} ({material.Name}) is in the wrong place, the palette is at {palette.Count}.");
        palette.Add(material);
    }

    /* The foliage. Every model is eight voxels a side and three colours, worked out of a few rules and a seed, the way
       the atlas is, so there's no file to keep in step with anything */

    private static void AddFoliage(VoxelPalette palette)
    {
        Vector3 green = VoxelPalette.Srgb(76, 132, 42), light = VoxelPalette.Srgb(104, 160, 58), dark = VoxelPalette.Srgb(56, 104, 32);

        Foliage(palette, GrassTuft, "grass tuft", Blades(new VoxelModel(dark, green, light), 7, 2, 5, 1));
        Foliage(palette, TallGrass, "tall grass", Blades(new VoxelModel(dark, green, light), 9, 4, 8, 2));
        Foliage(palette, Poppy, "poppy", Flower(VoxelPalette.Srgb(210, 36, 30), VoxelPalette.Srgb(40, 24, 20), 5));
        Foliage(palette, Dandelion, "dandelion", Flower(VoxelPalette.Srgb(246, 214, 46), VoxelPalette.Srgb(220, 160, 20), 4));
        Foliage(palette, Cornflower, "cornflower", Flower(VoxelPalette.Srgb(60, 96, 220), VoxelPalette.Srgb(30, 40, 110), 5));
        Foliage(palette, Daisy, "daisy", Flower(VoxelPalette.Srgb(240, 240, 236), VoxelPalette.Srgb(240, 200, 40), 4));
        Foliage(palette, Fern, "fern", Fronds(new VoxelModel(VoxelPalette.Srgb(44, 96, 36), VoxelPalette.Srgb(64, 124, 46), VoxelPalette.Srgb(84, 146, 56))));
        Foliage(palette, DeadBush, "dead bush", Bush(new VoxelModel(VoxelPalette.Srgb(110, 78, 40), VoxelPalette.Srgb(140, 104, 58))));
        Foliage(palette, RedMushroom, "red mushroom", Mushroom(VoxelPalette.Srgb(200, 40, 34), VoxelPalette.Srgb(240, 236, 226)));
        Foliage(palette, BrownMushroom, "brown mushroom", Mushroom(VoxelPalette.Srgb(150, 110, 76), VoxelPalette.Srgb(180, 140, 100)));
    }

    private static void Foliage(VoxelPalette palette, byte at, string name, VoxelModel model)
    {
        palette.Models.Add(model);
        Put(palette, at, new VoxelMaterial
        {
            Name = name,
            Kind = VoxelKind.Foliage,
            Color = model.Colors[1],
            TopTile = VoxelPalette.NO_TILE,
            SideTile = VoxelPalette.NO_TILE,
            BottomTile = VoxelPalette.NO_TILE,
            Model = (byte)(palette.Models.Count - 1),
            Roughness = 1.0f,
            Sways = true
        });
    }

    /// <summary>Blades of grass, standing up from the bottom at a few places, leaning a bit as they go up.</summary>
    private static VoxelModel Blades(VoxelModel model, int count, int shortest, int tallest, int seed)
    {
        var random = new Random(seed * 7919);
        for (int i = 0; i < count; i++)
        {
            int x = random.Next(1, 7), z = random.Next(1, 7);
            int height = random.Next(shortest, tallest + 1);
            int leanX = random.Next(-1, 2), leanZ = random.Next(-1, 2);
            for (int y = 0; y < height; y++)
            {
                int at = y * 2 / Math.Max(height, 1);
                int color = y < height / 3 ? 1 : (y < height * 2 / 3 ? 2 : 3);
                model.Set(x + leanX * at, y, z + leanZ * at, color);
            }
        }

        return model;
    }

    /// <summary>A stem with a leaf on it and a flower on top, a cross of petals round the middle.</summary>
    private static VoxelModel Flower(Vector3 petal, Vector3 middle, int stem)
    {
        var model = new VoxelModel(VoxelPalette.Srgb(60, 120, 40), petal, middle);
        for (int y = 0; y < stem; y++) model.Set(3, y, 4, 1);
        model.Set(4, 1, 4, 1);
        model.Set(5, 2, 4, 1);
        model.Set(2, 2, 4, 1);

        int top = stem;
        model.Set(3, top, 4, 3);
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) model.Set(3 + dx, top, 4 + dz, 2);
        foreach (var (dx, dz) in new[] { (1, 1), (-1, -1), (1, -1), (-1, 1) }) model.Set(3 + dx, top, 4 + dz, 2);
        model.Set(3, top + 1, 4, 2);
        return model;
    }

    /// <summary>Fronds out from the middle in four directions, arching down at their ends.</summary>
    private static VoxelModel Fronds(VoxelModel model)
    {
        for (int y = 0; y < 2; y++) model.Set(3, y, 3, 1);
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1) })
        {
            for (int k = 1; k <= 4; k++)
            {
                int y = k <= 2 ? 1 + k : 4 - (k - 2);
                model.Set(3 + dx * k, y, 3 + dz * k, k < 3 ? 2 : 3);
            }
        }

        return model;
    }

    /// <summary>A few dry branches out of the ground, bending outwards.</summary>
    private static VoxelModel Bush(VoxelModel model)
    {
        var random = new Random(4243);
        for (int i = 0; i < 6; i++)
        {
            int dx = random.Next(-1, 2), dz = random.Next(-1, 2);
            int x = 3, z = 3;
            for (int y = 0; y < 6; y++)
            {
                if (y % 2 == 1)
                {
                    x = Math.Clamp(x + dx, 0, 7);
                    z = Math.Clamp(z + dz, 0, 7);
                }

                model.Set(x, y, z, y > 3 ? 2 : 1);
            }
        }

        return model;
    }

    /// <summary>A pale stem and a round cap over it, spotted if the colours say so.</summary>
    private static VoxelModel Mushroom(Vector3 cap, Vector3 spots)
    {
        var model = new VoxelModel(VoxelPalette.Srgb(226, 220, 204), cap, spots);
        for (int y = 0; y < 3; y++) model.Set(3, y, 3, 1);
        for (int x = 1; x <= 5; x++)
        for (int z = 1; z <= 5; z++)
        {
            int dx = x - 3, dz = z - 3;
            if (dx * dx + dz * dz > 5) continue;
            model.Set(x, 3, z, 2);
            if (dx * dx + dz * dz <= 2) model.Set(x, 4, z, (x + z) % 3 == 0 ? 3 : 2);
        }

        model.Set(2, 4, 3, 3);
        model.Set(4, 3, 5, 3);
        return model;
    }
}
