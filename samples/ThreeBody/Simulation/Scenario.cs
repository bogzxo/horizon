using System.Text.Json;

namespace ThreeBody.Simulation;

/// <summary>One body as stored in a scenario file.</summary>
public sealed class BodyData
{
    public string Name { get; set; } = "";
    public double Mass { get; set; } = 1.0;
    public double X { get; set; }
    public double Y { get; set; }
    public double VX { get; set; }
    public double VY { get; set; }
    public double Radius { get; set; } = 0.05;
    /// <summary>RGBA, 0 to 1.</summary>
    public float[] Color { get; set; } = [1f, 1f, 1f, 1f];
}

/// <summary>A saved simulation state: the bodies and the settings that shape how they evolve.</summary>
public sealed class ScenarioData
{
    public string Name { get; set; } = "";
    /// <summary>Name of an <see cref="Simulation.Integrator"/> value.</summary>
    public string Integrator { get; set; } = nameof(Simulation.Integrator.VelocityVerlet);
    /// <summary>Name of a <see cref="CollisionMode"/> value.</summary>
    public string Collisions { get; set; } = nameof(CollisionMode.None);
    public double TimeScale { get; set; } = 1.0;
    public List<BodyData> Bodies { get; set; } = new();
}

/// <summary>Captures scenarios from a running system and reads and writes them as JSON.</summary>
public static class ScenarioStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ScenarioData Capture(string name, NBodySystem sim)
    {
        var data = new ScenarioData
        {
            Name = name,
            Integrator = sim.Integrator.ToString(),
            Collisions = sim.Collisions.ToString(),
            TimeScale = sim.TimeScale,
        };
        foreach (var b in sim.Bodies)
        {
            data.Bodies.Add(new BodyData
            {
                Name = b.Name, Mass = b.Mass, X = b.X, Y = b.Y, VX = b.VX, VY = b.VY, Radius = b.Radius,
                Color = [b.Color.X, b.Color.Y, b.Color.Z, b.Color.W],
            });
        }
        return data;
    }

    public static string ToJson(ScenarioData data) => JsonSerializer.Serialize(data, Options);

    /// <summary>Null when the text is not a usable scenario.</summary>
    public static ScenarioData? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<ScenarioData>(json, Options);
            if (data == null) return null;
            // Explicit nulls in the file overwrite the defaults, put them back
            data.Name ??= "";
            data.Integrator ??= nameof(Simulation.Integrator.VelocityVerlet);
            data.Collisions ??= nameof(CollisionMode.None);
            data.Bodies ??= new();
            data.Bodies.RemoveAll(b => b == null);
            foreach (var b in data.Bodies) b.Name ??= "";
            return data;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static void Save(string path, ScenarioData data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson(data));
    }

    /// <summary>Null when the file is missing, unreadable or not a scenario.</summary>
    public static ScenarioData? Load(string path)
    {
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Scenario names in a directory (*.json without the extension), sorted. Empty when there is no such directory.</summary>
    public static IReadOnlyList<string> List(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            var names = new List<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
