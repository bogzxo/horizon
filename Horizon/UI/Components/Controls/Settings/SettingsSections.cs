using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// Hex's one settings file, a <c>settings</c> object in sections, the terminal's (Dusk and Dawn's terminal page), the
/// editor's (Dawn) and the extensions'. Each side reads its own section and writes only that, the others kept as the
/// file has them at that moment (read again just before, so Dawn and Dusk running at once don't write over each other),
/// the terminal's help over every one of its keys whoever writes. A file from before the sections (the terminal's
/// keys at the top) is read as the terminal's section and written in sections the next time.
/// </summary>
public static class SettingsSections
{
    public const string TERMINAL = "terminal", EDITOR = "editor", EXTENSIONS = "extensions";

    private static readonly string[] Order = [TERMINAL, EDITOR, EXTENSIONS];

    /// <summary>The settings object a file declares, null for a file that isn't there or doesn't read.</summary>
    public static Dictionary<string, IRuntimeValue>? Read(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            return ReadText(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The settings object of a file's text. Throws when it doesn't declare one.</summary>
    public static Dictionary<string, IRuntimeValue> ReadText(string text)
    {
        HIDLRuntime runtime = new() { Output = null };
        var (success, message) = runtime.Evaluate(text);
        if (!success || runtime.UserScope.Lookup("settings") is not ObjectValue settings)
            throw new InvalidDataException(success ? "it has to declare an object called 'settings'" : message);
        return settings.Properties;
    }

    /// <summary>Whether a settings object is in sections, rather than the terminal's keys at the top as before.</summary>
    public static bool IsSectioned(Dictionary<string, IRuntimeValue> settings) =>
        Array.Exists(Order, name => settings.TryGetValue(name, out IRuntimeValue? value) && value is ObjectValue);

    /// <summary>A section of a settings object, the whole of an old one for the terminal's, empty when it isn't there.</summary>
    public static Dictionary<string, IRuntimeValue> Section(Dictionary<string, IRuntimeValue> settings, string name)
    {
        if (settings.TryGetValue(name, out IRuntimeValue? value) && value is ObjectValue section)
            return section.Properties;
        if (name == TERMINAL && !IsSectioned(settings))
            return settings;
        return new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The file's text with these sections written as given and the rest as the file at <paramref name="path"/> has them
    /// now (none when there is no file).
    /// </summary>
    public static string Write(string? path, params (string Name, HIDLObject Section)[] sections)
    {
        var kept = Read(path) ?? [];
        var settings = new HIDLObject();
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (string name in Order.Concat(Keys(kept)).Concat(sections.Select(section => section.Name)))
        {
            if (!written.Add(name))
                continue;

            int given = Array.FindIndex(sections, section => section.Name == name);
            if (given >= 0)
            {
                settings.Add(name, sections[given].Section, About(name));
                continue;
            }

            Dictionary<string, IRuntimeValue> section = Section(kept, name);
            if (section.Count == 0 && !kept.ContainsKey(name))
                continue;
            settings.Add(name, Commented(name, section), About(name));
        }

        return new HIDLDocument()
            .Comment("Hex's settings, Dawn's and Dusk's both, in sections. Written when something is changed from inside, read\n" +
                "again the moment it is saved. Edit it by hand all you like, a line that is missing is left at what it comes as.")
            .Define("dusk")
            .Define("dusk_settings")
            .Define("hex_settings")
            .Let("settings", settings)
            .Write();
    }

    // the sections an old or a sectioned file has, an old one's being the terminal's
    private static IEnumerable<string> Keys(Dictionary<string, IRuntimeValue> kept) =>
        IsSectioned(kept) ? kept.Where(pair => pair.Value is ObjectValue).Select(pair => pair.Key) : kept.Count > 0 ? [TERMINAL] : [];

    private static string? About(string section) => section switch
    {
        TERMINAL => "The terminal, Dusk and Dawn's terminal page.",
        EDITOR => "The editor, Dawn.",
        EXTENSIONS => "What every extension of Dawn's keeps, by its id.",
        _ => null
    };

    /// <summary>
    /// What a section's keys are, by the section, written over each of them as the file is saved. The terminal says its
    /// own (TerminalKinds), the engine doesn't know them.
    /// </summary>
    public static readonly Dictionary<string, Func<string, string?>> Help = [];

    // a section kept as the file had it, with its help over every key again when it has some
    private static HIDLObject Commented(string name, Dictionary<string, IRuntimeValue> section)
    {
        Help.TryGetValue(name, out Func<string, string?>? help);
        var made = new HIDLObject();
        foreach (var (key, value) in section)
            made.Add(key, value, help?.Invoke(key));
        return made;
    }
}
