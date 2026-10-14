using System.Collections.Frozen;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Logging;

namespace Horizon.Content;

/// <summary>
/// Every text a game puts in front of somebody, out of a file per language instead of out of the code, so the game can
/// be in another one by writing another file. A language is a .hor file in a folder, named after it (<c>en-gb.hor</c>),
/// that declares <c>strings</c>, an object of texts that can have objects of texts in it, which is how they are named.
/// <code>
/// let strings = {
///     main_menu: {
///         quit: "Quit",
///         welcome: "Hello {name}, ready?"
///     }
/// };
///
/// Localisation.Load("Assets/ui/localisation", "en-gb");
/// label.Text = Localisation.Get("main_menu.quit");
/// label.Text = Localisation.Format("main_menu.welcome", ("name", player.Name));
/// </code>
/// A layout names a text with an @ in front of the key (<c>label: "@main_menu.quit"</c>), the UI swaps it for the text
/// as it is measured and drawn, so the key is what stays in the file and what an editor shows and writes back. A text
/// that isn't in the file comes out as its key, said once in the log, so a missing one is plain to see and never a crash.
/// <para>
/// The texts are swapped wholesale on a load, so they can be read from any thread while another one loads a language.
/// </para>
/// </summary>
public static class Localisation
{
    /// <summary>The language a game comes up in when nobody says otherwise.</summary>
    public const string DEFAULT_LANGUAGE = "en-gb";

    /// <summary>What a language file declares its texts as.</summary>
    public const string STRINGS = "strings";

    /// <summary>What a text in a layout starts with when it is a key and not the text itself.</summary>
    public const char KEY_MARK = '@';

    private static FrozenDictionary<string, string> texts = FrozenDictionary<string, string>.Empty;

    // The keys that were asked for and aren't there, said in the log once each
    private static readonly HashSet<string> missing = [];
    private static readonly Lock missingLock = new();

    /// <summary>The language that was loaded last, empty for none.</summary>
    public static string Language { get; private set; } = string.Empty;

    /// <summary>The folder the language was loaded out of, empty for none.</summary>
    public static string Folder { get; private set; } = string.Empty;

    /// <summary>How many texts the language has.</summary>
    public static int Count => texts.Count;

    /// <summary>Called after a language has been loaded, for whatever put texts on screen that aren't keys to swap them.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Loads a language out of a folder, which replaces whatever was loaded before. A file with something wrong with it is
    /// said in the log and leaves the texts there were.
    /// </summary>
    /// <returns>Whether it loaded.</returns>
    public static bool Load(string folder, string language = DEFAULT_LANGUAGE)
    {
        string file = Path.Combine(folder, language + ".hor");
        if (!File.Exists(file))
        {
            Log.Error($"[Localisation] There is no {language} in {folder}, there is no {file}.");
            return false;
        }

        try
        {
            texts = Read(File.ReadAllText(file), file);
        }
        catch (Exception e)
        {
            Log.Error($"[Localisation] {e.Message}");
            return false;
        }

        lock (missingLock)
            missing.Clear();

        // The other languages are read again from the new folder, or after a change, as they are asked for
        lock (othersLock)
            others.Clear();

        Language = language;
        Folder = folder;
        Log.Info($"[Localisation] {language}, {texts.Count} texts out of {folder}.");

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The languages there are in a folder, the names of its .hor files, in order. Empty for a folder that isn't there.
    /// </summary>
    public static IReadOnlyList<string> Available(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*.hor").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order()]
            : [];

    /// <summary>
    /// Helper method to find the folder of languages for a file of a game, a layout say, by looking for a folder called
    /// localisation next to it and in the folders above it, a few of them. Null if there is none, for an editor that has
    /// a layout open and wants it to read the way the game reads it.
    /// </summary>
    public static string? FindNear(string path, int levels = 4)
    {
        string? at = Path.GetDirectoryName(Path.GetFullPath(path));

        for (int i = 0; i <= levels && at is not null; i++, at = Path.GetDirectoryName(at))
        {
            string here = Path.Combine(at, "localisation");
            if (Directory.Exists(here))
                return here;

            // A layout in a folder next to the languages, ui/layouts and ui/localisation
            foreach (string sibling in Directory.GetDirectories(at))
            {
                string inside = Path.Combine(sibling, "localisation");
                if (Directory.Exists(inside))
                    return inside;
            }
        }

        return null;
    }

    /// <summary>The languages there are in the folder the loaded one came out of, empty with none loaded.</summary>
    public static IReadOnlyList<string> Languages => Folder.Length > 0 ? Available(Folder) : [];

    // The other languages of the folder, read the first time something asks what they say, by the file they are in
    private static readonly Dictionary<string, FrozenDictionary<string, string>?> others = [];
    private static readonly Lock othersLock = new();

    /// <summary>
    /// What a key says in another language of the loaded one's folder, without loading it, null if it hasn't got it. For
    /// an editor that shows a key in every language there is. Each is read once.
    /// </summary>
    public static string? In(string language, string key)
    {
        if (language == Language)
            return texts.TryGetValue(key, out string? here) ? here : null;

        string file = Path.Combine(Folder, language + ".hor");
        FrozenDictionary<string, string>? read;
        lock (othersLock)
        {
            if (!others.TryGetValue(file, out read))
            {
                try
                {
                    read = File.Exists(file) ? Read(File.ReadAllText(file), file) : null;
                }
                catch (Exception e)
                {
                    Log.Warning($"[Localisation] {e.Message}");
                    read = null;
                }

                others[file] = read;
            }
        }

        return read is not null && read.TryGetValue(key, out string? said) ? said : null;
    }

    /// <summary>Every key the language has, for whatever offers them to somebody typing one.</summary>
    public static IEnumerable<string> Keys => texts.Keys;

    /// <summary>Whether the language has a text by that key.</summary>
    public static bool Has(string key) => texts.ContainsKey(key);

    /// <summary>The text by a key, the key itself if there is none (and the log says so, once).</summary>
    public static string Get(string key)
    {
        if (texts.TryGetValue(key, out string? text))
            return text;

        Missing(key);
        return key;
    }

    /// <summary>
    /// The text by a key with the names in braces in it filled in, <c>"{name} takes the round"</c> with
    /// <c>("name", "Ryu")</c>. A name it has nothing for is left as it is written.
    /// </summary>
    public static string Format(string key, params ReadOnlySpan<(string Name, object? Value)> values)
    {
        string text = Get(key);
        if (values.Length == 0 || !text.Contains('{'))
            return text;

        var built = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            int close;
            if (text[i] != '{' || (close = text.IndexOf('}', i + 1)) < 0)
            {
                built.Append(text[i]);
                continue;
            }

            ReadOnlySpan<char> name = text.AsSpan(i + 1, close - i - 1);
            bool filled = false;

            foreach (var (wanted, value) in values)
            {
                if (!name.SequenceEqual(wanted))
                    continue;

                built.Append(value);
                filled = true;
                break;
            }

            if (filled)
                i = close;
            else
                built.Append(text[i]);
        }

        return built.ToString();
    }

    /// <summary>
    /// What a text is to be shown as. One that starts with an @ is a key and comes out as the text by that key, anything
    /// else is the text itself. Two @s at the start are one @ written on purpose. Makes no garbage, it is called every
    /// time the UI is measured and painted.
    /// </summary>
    public static string Resolve(string text)
    {
        if (text.Length < 2 || text[0] != KEY_MARK)
            return text;

        if (text[1] == KEY_MARK)
            return EscapedOf(text);

        var lookup = texts.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text.AsSpan(1), out string? found))
            return found;

        Missing(text);
        return text;
    }

    // Texts that start with an @ written on purpose, kept once so showing one makes no garbage every paint
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> escaped = new();

    private static string EscapedOf(string text) => escaped.GetOrAdd(text, static written => written[1..]);

    /// <summary>Whether a text is a key (starts with an @) rather than the text itself.</summary>
    public static bool IsKey(string text) => text.Length > 1 && text[0] == KEY_MARK && text[1] != KEY_MARK;

    private static void Missing(string key)
    {
        lock (missingLock)
        {
            if (!missing.Add(key))
                return;
        }

        if (Language.Length > 0)
            Log.Warning($"[Localisation] {Language} has nothing for {key}.");
    }

    /// <summary>
    /// Helper method to read a language file, the texts in it by their keys, the keys of what is inside of an object
    /// joined to its own with a dot.
    /// </summary>
    internal static FrozenDictionary<string, string> Read(string source, string file)
    {
        var runtime = new HIDLRuntime();
        var (success, result) = runtime.Evaluate(source);
        if (!success)
            throw new Exception($"{file} didn't run, {result}");

        if (runtime.UserScope.Lookup(STRINGS) is not ObjectValue { Properties: { } strings })
            throw new Exception($"{file} has to declare an object called {STRINGS}.");

        var read = new Dictionary<string, string>();
        Flatten(strings, string.Empty, read, file);
        return read.ToFrozenDictionary();
    }

    private static string JoinLines(ListValue lines, string key, string file)
    {
        var joined = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] is not StringValue line)
                throw new Exception($"{file} has a line of {key} that isn't a text.");

            if (i > 0) joined.Append('\n');
            joined.Append(line.Value);
        }

        return joined.ToString();
    }

    /// <summary>
    /// Reads a file of texts the way a language is read, without loading it as the language. For texts a program keeps
    /// apart from the game's (an editor's own help, while the game's language is loaded for its layouts).
    /// </summary>
    /// <exception cref="Exception">The file isn't there, didn't run, or isn't texts, the message says which.</exception>
    public static IReadOnlyDictionary<string, string> ReadFile(string path) => Read(File.ReadAllText(path), path);

    private static void Flatten(Dictionary<string, IRuntimeValue> values, string prefix, Dictionary<string, string> into, string file)
    {
        foreach (var (name, value) in values)
        {
            string key = prefix.Length > 0 ? prefix + "." + name : name;

            switch (value)
            {
                case StringValue text:
                    into[key] = text.Value;
                    break;

                case ObjectValue { Properties: { } inside }:
                    Flatten(inside, key, into, file);
                    break;

                // A long text a line to a text of the list, the lines joined, so a page of help is written the way it
                // reads and coloured the way code is, and not one line a screen wide or a string that runs over lines
                case ListValue lines:
                    into[key] = JoinLines(lines, key, file);
                    break;

                default:
                    throw new Exception($"{file} has {key} as something that isn't a text, a list of lines of one, or an object of them.");
            }
        }
    }
}
