using System.Text.Json;

using Horizon.Rendering.Voxels;

namespace AutoVoxel.Gameplay;

/// <summary>
/// What the player set on the options screen, kept in settings.json next to the game. Read once as it starts, written
/// whenever something changes. Applied to a renderer with <see cref="Apply"/>.
/// </summary>
public sealed class GameSettings
{
    private const string FILE = "settings.json";

    public static GameSettings Current { get; private set; } = new();

    /// <summary>How much of the screen is traced, 1 is every pixel.</summary>
    public float RenderScale { get; set; } = 1.0f;

    public bool BounceLight { get; set; } = true;

    /// <summary>0 low to 3 ultra, how far off things stay sharp.</summary>
    public int Detail { get; set; } = 2;

    public float FieldOfView { get; set; } = 75.0f;

    public float MouseSensitivity { get; set; } = 1.0f;

    public bool Bloom { get; set; } = true;

    public bool VSync { get; set; } = true;

    public bool Textures { get; set; } = true;

    /// <summary>How long a whole day is, in minutes.</summary>
    public float DayMinutes { get; set; } = 20.0f;

    public bool AutoJump { get; set; }

    /// <summary>How big a world New world makes, in blocks across.</summary>
    public int WorldSize { get; set; } = 2048;

    public static readonly string[] DetailNames = ["Low", "Medium", "High", "Ultra"];

    private static readonly float[] DetailBias = [3.0f, 2.0f, 1.5f, 1.0f];

    public static readonly float[] Scales = [0.5f, 0.67f, 0.75f, 0.85f, 1.0f];

    public static readonly int[] WorldSizes = [1024, 2048, 4096, 8192];

    public void Apply(VoxelRenderer renderer)
    {
        renderer.RenderScale = RenderScale;
        renderer.BounceLight = BounceLight;
        renderer.Detail = DetailBias[Math.Clamp(Detail, 0, DetailBias.Length - 1)];
        renderer.MaxSteps = Detail switch { 0 => 256, 1 => 320, 2 => 400, _ => 512 };
        renderer.Bloom = Bloom ? 0.05f : 0.0f;
        renderer.Textures = Textures;
    }

    public static void Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, FILE);
        if (!File.Exists(path)) return;

        try
        {
            Current = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path)) ?? new GameSettings();
        }
        catch (Exception e)
        {
            Horizon.Logging.Log.Warning($"[Settings] {FILE} couldn't be read ({e.Message}), starting with the usual settings.");
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, FILE), JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Horizon.Logging.Log.Warning($"[Settings] {FILE} couldn't be written: {e.Message}");
        }
    }
}
