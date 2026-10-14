using Horizon.Logging;
using Horizon.UI.Scripting;

namespace Horizon.UI.Skinning;

/// <summary>
/// A skin there is to pick, a folder under Assets/uix with a skin.hor in it. Nothing of it is loaded, this is
/// only what its file says about itself.
/// </summary>
/// <param name="Name">What it is picked by, the name of its folder ("dead_revolver", "retro_windows").</param>
/// <param name="Title">What to call it in front of a player, the <c>title</c> of its file, its name if it has none.</param>
/// <param name="Directory">The folder, the way <see cref="UICompositor.SetSkin"/> wants it.</param>
/// <param name="File">The skin file in it.</param>
/// <param name="Themes">The themes it comes in, none for a skin that is one look.</param>
/// <param name="UsualTheme">The theme it is in when nobody says, null for a skin without themes.</param>
public sealed record UISkinChoice(string Name, string Title, string Directory, string File, IReadOnlyList<string> Themes, string? UsualTheme)
{
    /// <summary>Whether it has a theme of that name.</summary>
    public bool Has(string? theme) => theme is not null && Themes.Contains(theme);
}

/// <summary>
/// The skin every UI is drawn in unless it asked for one of its own, and the list of the ones there are to choose
/// from. One setting for the whole game, a UI made with <c>new UICompositor(camera)</c> follows it and changes
/// its look the moment it changes, whichever scene it is in.
/// <code>
/// UISkins.Use("retro_windows");              // every UI, from the next frame on
/// UISkins.Use("kenney", "red");              // a skin and one of its themes
/// foreach (var skin in UISkins.Available)    // what to put in an options menu
///     Console.WriteLine($"{skin.Title} ({string.Join(", ", skin.Themes)})");
/// </code>
/// A UI that was made with a skin of its own (a tool in the flat skin, say) keeps it whatever this says. One that
/// was made with only a theme ("gold") gets that theme of whichever skin this is, where the skin has one of that
/// name, and the theme set here where it hasn't. From any thread.
/// </summary>
public static class UISkins
{
    /// <summary>Where the skins that come with the engine are, and where a game puts its own to have them listed.</summary>
    public const string ROOT = "Assets/uix/";

    /// <summary>What a skin calls its file.</summary>
    public const string FILE = "skin.hor";

    /// <summary>The skin every UI has until somebody picks another.</summary>
    public const string DEFAULT = "dead_revolver";

    private static readonly Lock gate = new();
    private static UISkinChoice[]? available;
    private static UISkinChoice? current;
    private static string? theme;
    private static int version;

    // Whether the skin was asked for through the environment, and so isn't anybody else's to change this run
    private static bool pinned;

    /// <summary>Raised when the skin or its theme has been changed, on the thread that changed it.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Every skin there is, in the order of their folders' names. Read off the disk the first time it is asked
    /// for, <see cref="Refresh"/> reads it again.
    /// </summary>
    public static IReadOnlyList<UISkinChoice> Available
    {
        get
        {
            lock (gate)
                return available ??= Scan();
        }
    }

    /// <summary>The skin every UI that follows this is drawn in.</summary>
    public static UISkinChoice Current
    {
        get
        {
            lock (gate)
                return current ??= Start();
        }
    }

    /// <summary>Which theme of <see cref="Current"/> it is, null for the one the skin says is its usual one.</summary>
    public static string? Theme
    {
        get
        {
            lock (gate)
                return theme;
        }
    }

    /// <summary>Goes up every time the skin or the theme changes, which is how a UI notices.</summary>
    public static int Version => Volatile.Read(ref version);

    /// <summary>Finds a skin by the name of its folder, null if there is none.</summary>
    public static UISkinChoice? Find(string name)
    {
        foreach (var choice in Available)
        {
            if (string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase))
                return choice;
        }

        return null;
    }

    /// <summary>
    /// Changes the skin of every UI that follows this one.
    /// </summary>
    /// <param name="name">The name of its folder, see <see cref="Available"/>.</param>
    /// <param name="theme">Which of its themes, null (or one it hasn't got) for its usual one.</param>
    /// <returns>Whether there is such a skin. If not nothing changes, and the log says so.</returns>
    public static bool Use(string name, string? theme = null)
    {
        if (Find(name) is not { } choice)
        {
            Log.Warning($"[UISkins] There is no skin called '{name}'. There are {string.Join(", ", Available.Select(skin => skin.Name))}.");
            return false;
        }

        Use(choice, theme);
        return true;
    }

    /// <inheritdoc cref="Use(string, string?)"/>
    public static void Use(UISkinChoice choice, string? theme = null)
    {
        // What the environment asked for stays whatever a game's options say, that is what asking there is for
        _ = Current;
        if (pinned)
            return;

        lock (gate)
        {
            string? wanted = choice.Has(theme) ? theme : null;
            if (current == choice && UISkins.theme == wanted)
                return;

            current = choice;
            UISkins.theme = wanted;
            Interlocked.Increment(ref version);
        }

        Changed?.Invoke();
    }

    /// <summary>Changes only the theme, of the skin there is. One the skin hasn't got is its usual one.</summary>
    public static void UseTheme(string? theme) => Use(Current, theme);

    /// <summary>
    /// Goes to the skin after the one there is (or before it, for a negative number), round and round, in its
    /// usual theme. What a key that flips through the skins calls.
    /// </summary>
    public static UISkinChoice Step(int by)
    {
        var all = Available;
        if (all.Count == 0)
            return Current;

        int at = Math.Max(0, IndexOf(all, Current));
        UISkinChoice next = all[((at + by) % all.Count + all.Count) % all.Count];
        Use(next);
        return next;
    }

    /// <summary>
    /// Goes to the theme after the one there is (or before it) of the skin there is, round and round.
    /// </summary>
    /// <returns>The theme it is now, null for a skin that has none.</returns>
    public static string? StepTheme(int by)
    {
        UISkinChoice skin = Current;
        if (skin.Themes.Count == 0)
            return null;

        string? now = Theme ?? skin.UsualTheme;
        int at = 0;
        for (int i = 0; i < skin.Themes.Count; i++)
        {
            if (skin.Themes[i] == now) at = i;
        }

        string next = skin.Themes[((at + by) % skin.Themes.Count + skin.Themes.Count) % skin.Themes.Count];
        Use(skin, next);
        return next;
    }

    private static int IndexOf(IReadOnlyList<UISkinChoice> all, UISkinChoice choice)
    {
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Name == choice.Name) return i;
        }

        return -1;
    }

    // Where the choice is kept between runs, for whoever asked for that
    private static string? remembered;

    /// <summary>
    /// Keeps the choice in a file, a line of "name:theme". Whatever the file says is put in force now, and every
    /// change from here on is written to it, so a tool comes back up in the skin it was left in. A game with an
    /// options file of its own keeps the two names in that instead and has no use for this.
    /// </summary>
    /// <param name="path">The file, which needn't be there yet. Its folder is made if it isn't. Null to stop keeping it, the file that was written stays as it is.</param>
    public static void Remember(string? path)
    {
        remembered = path;
        if (path is null)
        {
            Changed -= Keep;
            return;
        }

        try
        {
            // What the environment asks for wins over what was left the last time, and isn't written over it
            if (System.Environment.GetEnvironmentVariable(SKIN_VARIABLE) is { Length: > 0 })
            {
                remembered = null;
                return;
            }

            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } kept)
            {
                string[] parts = kept.Split(':', 2);
                if (Find(parts[0]) is { } skin)
                    Use(skin, parts.Length > 1 ? parts[1] : null);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[UISkins] '{path}' can't be read, {e.Message}");
        }

        Changed -= Keep;
        Changed += Keep;
    }

    /// <summary>Helper method to write the choice to the file it is kept in.</summary>
    private static void Keep()
    {
        if (remembered is not { } path)
            return;

        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
                System.IO.Directory.CreateDirectory(folder);

            UISkinChoice skin = Current;
            File.WriteAllText(path, Theme is { } theme ? $"{skin.Name}:{theme}" : skin.Name);
        }
        catch (Exception e)
        {
            Log.Warning($"[UISkins] '{path}' can't be written, {e.Message}");
        }
    }

    /// <summary>
    /// The skin and theme a UI that asked for a theme of its own ends up with, which is that theme where the skin
    /// has it and the one set here where it hasn't.
    /// </summary>
    internal static (string Directory, string File, string? Theme) Resolve(string? wanted)
    {
        lock (gate)
        {
            UISkinChoice skin = current ??= Start();
            return (skin.Directory, skin.File, skin.Has(wanted) ? wanted : theme);
        }
    }

    /// <summary>Reads the list of skins off the disk again, for a tool that had one added while it ran.</summary>
    public static void Refresh()
    {
        lock (gate)
            available = Scan();
    }

    // Set this in the environment to the name of a skin, or "name:theme", and the game starts in it whatever it
    // would have picked, for looking at a game in a skin without going through its options
    private const string SKIN_VARIABLE = "HORIZON_UI_SKIN";

    /// <summary>Helper method to work out the skin a game starts in. Inside of the gate.</summary>
    private static UISkinChoice Start()
    {
        if (System.Environment.GetEnvironmentVariable(SKIN_VARIABLE) is { Length: > 0 } wanted)
        {
            string[] parts = wanted.Split(':', 2);
            if (Find(parts[0]) is { } asked)
            {
                pinned = true;
                theme = parts.Length > 1 && asked.Has(parts[1]) ? parts[1] : null;
                return asked;
            }

            Log.Warning($"[UISkins] {SKIN_VARIABLE} asks for '{parts[0]}' and there is no such skin.");
        }

        return Find(DEFAULT) ?? Fallback();
    }

    /// <summary>What a UI is drawn in when the folder of skins is nowhere to be found, the paths the engine always used.</summary>
    private static UISkinChoice Fallback() =>
        new(DEFAULT, DEFAULT, UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE, [], null);

    private static UISkinChoice[] Scan()
    {
        var found = new List<UISkinChoice>();
        if (!System.IO.Directory.Exists(ROOT))
            return [];

        foreach (string folder in System.IO.Directory.GetDirectories(ROOT).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!System.IO.File.Exists(Path.Combine(folder, FILE)))
                continue;

            string name = Path.GetFileName(folder.TrimEnd('/', '\\'));
            string directory = $"{ROOT}{name}/";

            try
            {
                var (title, themes, usual) = UISkin.Describe(directory, FILE);
                found.Add(new UISkinChoice(name, title ?? name, directory, FILE, themes, usual));
            }
            catch (Exception e)
            {
                Log.Warning($"[UISkins] '{directory}{FILE}' can't be read and is left out of the list, {e.Message}");
            }
        }

        return [.. found];
    }
}
