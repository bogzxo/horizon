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
    public static Dictionary<string, IRuntimeValue>? Read(string? path) => TryRead(path, out _);

    /// <summary>
    /// The settings object a file declares, null for a file that isn't there or doesn't read. One that doesn't read says
    /// why in <paramref name="error"/>, what HIDL said with the line it's on, a file that isn't there says nothing.
    /// </summary>
    public static Dictionary<string, IRuntimeValue>? TryRead(string? path, out string? error)
    {
        error = null;
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            return ReadText(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// The settings object of a file's text. Throws a <see cref="SettingsReadException"/> when it doesn't declare one,
    /// with the line and column HIDL said when there's one. A file without the <c>settings</c> object round it is read too,
    /// one that declares <c>let editor = { ... }</c> (or terminal, or extensions) at the top, or that is only the inside of
    /// the object, the editor section in its braces and nothing round it. It's written back with the object round it.
    /// </summary>
    public static Dictionary<string, IRuntimeValue> ReadText(string text)
    {
        HIDLRuntime runtime = new() { Output = null };
        try
        {
            runtime.Run(text);
        }
        catch (Exception e)
        {
            // What it says is about the file as it is, not the one with the object put round it
            return Unwrapped(text) ?? throw Placed(e);
        }

        if (runtime.UserScope.Lookup("settings") is ObjectValue settings)
            return settings.Properties;

        // The sections declared at the top without the object round them
        var loose = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (string name in Order)
        {
            if (runtime.UserScope.Variables.TryGetValue(name, out IRuntimeValue? declared) && declared is ObjectValue section)
                loose[name] = section;
        }

        if (loose.Count > 0)
            return loose;
        return Unwrapped(text) ?? throw new SettingsReadException(Missing, 0, 0);
    }

    /// <summary>
    /// What's said when a file declares no settings at all, the skeleton it needs written the way the file is. A program
    /// sets its own (Hex has it in its strings), this is what the engine says by itself.
    /// </summary>
    public static string Missing { get; set; } = "it has to declare settings with an editor section, like let settings = { editor: { tab_size: 4 } };";

    // The text read as the inside of the settings object, null when that doesn't read either or has none of the sections
    private static Dictionary<string, IRuntimeValue>? Unwrapped(string text)
    {
        HIDLRuntime runtime = new() { Output = null };
        try
        {
            runtime.Run("let settings = {\n" + text + "\n};");
        }
        catch (Exception)
        {
            return null;
        }

        return runtime.UserScope.Lookup("settings") is ObjectValue settings && IsSectioned(settings.Properties) ? settings.Properties : null;
    }

    // What went wrong with where it went wrong, when HIDL knows
    private static SettingsReadException Placed(Exception e) => e switch
    {
        SettingsReadException already => already,
        Horizon.HIDL.Parsing.ParseException parse => new SettingsReadException(e.Message, parse.Line, parse.Col),
        HidlRuntimeException run => new SettingsReadException(e.Message, run.Line, run.Column),
        _ => new SettingsReadException(e.Message, 0, 0)
    };

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

/// <summary>
/// A settings file that doesn't read, what's wrong with it and where, the line and the column from 1 (0 when it's the
/// whole file, one that declares no settings).
/// </summary>
public sealed class SettingsReadException(string message, int line, int column) : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}
