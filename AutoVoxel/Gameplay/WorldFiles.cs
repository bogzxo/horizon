using System.Numerics;
using System.Text.Json;

using Horizon.Rendering.Voxels;

namespace AutoVoxel.Gameplay;

/// <summary>What a world is apart from its voxels, kept next to them. The seed, where the player was and when it was.</summary>
public sealed class WorldInfo
{
    public string Name { get; set; } = "world";
    public int Seed { get; set; }
    public int Size { get; set; }
    public float PlayerX { get; set; }
    public float PlayerY { get; set; }
    public float PlayerZ { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float TimeOfDay { get; set; } = 9.0f;
    public bool Flying { get; set; }
    public DateTime Saved { get; set; }

    public Vector3 Player => new(PlayerX, PlayerY, PlayerZ);
}

/// <summary>
/// Where worlds are kept, %LocalAppData%/AutoVoxel/worlds, two files each, the voxels (<see cref="VoxelTree.Save"/>,
/// squeezed) and a little json of everything else.
/// </summary>
public static class WorldFiles
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVoxel", "worlds");

    private static string VoxelsOf(string name) => Path.Combine(Directory, name + ".voxels");

    private static string InfoOf(string name) => Path.Combine(Directory, name + ".json");

    // Which columns of a world that isn't all made yet are made, a byte each, only there while it isn't
    private static string ColumnsOf(string name) => Path.Combine(Directory, name + ".columns");

    /// <summary>The most recently saved world, null if there's none.</summary>
    public static WorldInfo? Latest()
    {
        if (!System.IO.Directory.Exists(Directory)) return null;

        WorldInfo? latest = null;
        foreach (string file in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize<WorldInfo>(File.ReadAllText(file));
                if (info is null || !File.Exists(VoxelsOf(info.Name))) continue;
                if (latest is null || info.Saved > latest.Saved) latest = info;
            }
            catch
            {
                // Somebody else's file, or a broken one, not ours to worry about
            }
        }

        return latest;
    }

    /// <summary>
    /// Saves a world. Not compacted, it's being played in and a compacted tree would have to go to the card all over
    /// again, it's compacted when it's read back instead. <paramref name="columns"/> is which columns are made, for a
    /// world that isn't all made yet, null for one that is.
    /// </summary>
    public static void Save(VoxelTree tree, WorldInfo info, bool[]? columns = null)
    {
        System.IO.Directory.CreateDirectory(Directory);
        info.Saved = DateTime.Now;

        // Written next to the old one and swapped in, so a crash halfway through doesn't lose the world
        string voxels = VoxelsOf(info.Name), temporary = voxels + ".tmp";
        using (var stream = File.Create(temporary)) tree.Save(stream, compact: false);
        File.Move(temporary, voxels, overwrite: true);
        File.WriteAllText(InfoOf(info.Name), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

        if (columns is not null && Array.IndexOf(columns, false) >= 0)
            File.WriteAllBytes(ColumnsOf(info.Name), [.. columns.Select(done => done ? (byte)1 : (byte)0)]);
        else if (File.Exists(ColumnsOf(info.Name)))
            File.Delete(ColumnsOf(info.Name));
    }

    /// <summary>Which columns of a saved world are made, null if all of them are.</summary>
    public static bool[]? LoadColumns(WorldInfo info)
    {
        string path = ColumnsOf(info.Name);
        return File.Exists(path) ? [.. File.ReadAllBytes(path).Select(b => b != 0)] : null;
    }

    public static VoxelTree Load(WorldInfo info, VoxelPalette palette)
    {
        using var stream = File.OpenRead(VoxelsOf(info.Name));
        return VoxelTree.Load(stream, palette);
    }
}
