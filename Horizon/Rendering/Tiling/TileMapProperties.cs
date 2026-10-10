using System.Globalization;
using System.Numerics;

using DotTiled;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// The custom properties something in a Tiled map was given, a layer, an object, a tile or the map itself.
/// Names don't mind their case, and every getter takes what to answer when the property isn't there (or can't be
/// read as what was asked for), so whoever reads a map doesn't have to parse anything or check for anything.
/// <code>
/// float radius = light.Properties.GetFloat("light_radius", 100);
/// Vector4 colour = light.Properties.GetColor("light_colour", Vector4.One);
/// </code>
/// A property that was typed in as text reads as a number or a yes-or-no just the same, as long as that is what it says.
/// </summary>
public sealed class TileMapProperties
{
    /// <summary>For whatever has none.</summary>
    public static TileMapProperties Empty { get; } = new(null, string.Empty);

    private readonly Dictionary<string, IProperty> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;

    /// <param name="properties">What Tiled has.</param>
    /// <param name="directory">Where the files a property names are looked for from.</param>
    /// <param name="fallback">Properties to look in for whatever these don't have, those of its tile for an object that shows one.</param>
    internal TileMapProperties(IEnumerable<IProperty>? properties, string directory, TileMapProperties? fallback = null)
    {
        this.directory = directory;

        if (fallback is not null)
        {
            foreach (var (name, property) in fallback.entries)
                entries[name] = property;
        }

        foreach (IProperty property in properties ?? [])
            entries[property.Name] = property;
    }

    public int Count => entries.Count;

    /// <summary>The names of the properties there are.</summary>
    public IEnumerable<string> Names => entries.Keys;

    public bool Has(string name) => entries.ContainsKey(name);

    /// <summary>The property as DotTiled read it, null if there is none by that name.</summary>
    public IProperty? Raw(string name) => entries.GetValueOrDefault(name);

    /// <summary>The property as text, whatever type it is.</summary>
    public string GetString(string name, string otherwise = "") => entries.GetValueOrDefault(name) switch
    {
        StringProperty text => text.Value,
        FileProperty file => file.Value,
        BoolProperty flag => flag.Value ? "true" : "false",
        IntProperty number => number.Value.ToString(CultureInfo.InvariantCulture),
        FloatProperty number => number.Value.ToString(CultureInfo.InvariantCulture),
        ObjectProperty reference => reference.Value.ToString(CultureInfo.InvariantCulture),
        EnumProperty choice => string.Join(",", choice.Value),
        ColorProperty { Value.HasValue: true } colour => $"#{colour.Value.Value.A:x2}{colour.Value.Value.R:x2}{colour.Value.Value.G:x2}{colour.Value.Value.B:x2}",
        _ => otherwise
    };

    public bool GetBool(string name, bool otherwise = false) => entries.GetValueOrDefault(name) switch
    {
        BoolProperty flag => flag.Value,
        IntProperty number => number.Value != 0,
        StringProperty text when bool.TryParse(text.Value, out bool value) => value,
        _ => otherwise
    };

    public float GetFloat(string name, float otherwise = 0.0f) => TryGetFloat(name, out float value) ? value : otherwise;

    public int GetInt(string name, int otherwise = 0) => TryGetFloat(name, out float value) ? (int)value : otherwise;

    public bool TryGetFloat(string name, out float value)
    {
        switch (entries.GetValueOrDefault(name))
        {
            case FloatProperty number:
                value = number.Value;
                return true;

            case IntProperty number:
                value = number.Value;
                return true;

            case StringProperty text:
                return float.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            default:
                value = 0.0f;
                return false;
        }
    }

    public bool TryGetString(string name, out string value)
    {
        value = GetString(name, null!);
        return value is not null;
    }

    /// <summary>A colour as red, green, blue and alpha from 0 to 1.</summary>
    public Vector4 GetColor(string name, Vector4 otherwise) => entries.GetValueOrDefault(name) switch
    {
        ColorProperty { Value.HasValue: true } colour => ToVector(colour.Value.Value),
        StringProperty text when TiledColor.TryParse(text.Value, CultureInfo.InvariantCulture, out TiledColor? parsed) && parsed is not null => ToVector(parsed),
        _ => otherwise
    };

    /// <summary>Where the file a file property names is, made out from where the map is. Empty if there is no such property.</summary>
    public string GetFile(string name)
    {
        string file = GetString(name);
        return file.Length > 0 ? Path.GetFullPath(Path.Combine(directory, file)) : string.Empty;
    }

    /// <summary>The id of the object an object property points at, 0 for none. See <see cref="TileMap.FindObject(int)"/>.</summary>
    public int GetObject(string name) => entries.GetValueOrDefault(name) is ObjectProperty reference ? (int)reference.Value : GetInt(name);

    /// <summary>The members of a property that is a class of its own, empty if it isn't one.</summary>
    public TileMapProperties GetClass(string name) =>
        entries.GetValueOrDefault(name) is ClassProperty members ? new TileMapProperties(members.Value, directory) : Empty;

    internal static Vector4 ToVector(TiledColor colour) =>
        new(colour.R / 255.0f, colour.G / 255.0f, colour.B / 255.0f, colour.A / 255.0f);
}
