using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

using Silk.NET.Input;

namespace Horizon.UI.Components;

/// <summary>
/// Something a terminal application can do, by name ("new_tab"), with a title for the palette and the menus, what it
/// does, the key it is on as a keys file writes it ("Ctrl+Shift+T", empty for none) and whoever registered it (a
/// plugin, or null for the application's own).
/// </summary>
public sealed class TerminalCommand(string name, string title, Action run, object? owner)
{
    public string Name { get; } = name;
    public string Title { get; set; } = title;
    public Action Run { get; set; } = run;
    public object? Owner { get; } = owner;

    /// <summary>The key it is on as the menus write it, empty for none.</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>The key read, null for none.</summary>
    public (Key Key, bool Control, bool Shift, bool Alt)? Parsed { get; private set; }

    /// <summary>Puts it on a key, empty for none. False for a text that isn't a key, which leaves it where it was.</summary>
    public bool Bind(string key)
    {
        if (key.Length == 0)
        {
            Key = string.Empty;
            Parsed = null;
            return true;
        }

        if (TerminalShortcut.Read(key) is not { } read)
            return false;

        Parsed = read;
        Key = TerminalShortcut.Write(read.Key, read.Control, read.Shift, read.Alt);
        return true;
    }
}

/// <summary>
/// The commands of a terminal application, by name, in the order they were registered, each on the key its keys
/// files say or the one it asked for. Keys files are HIDL, <c>let keys = { new_tab: "Ctrl+Shift+T" }</c>, read one
/// over the other (the ones that come with the program, then the user's), and they win over what a command asks for
/// when it is registered, so a key moved in a file stays moved whatever registers it. Nothing here runs keys off the
/// keyboard, <see cref="Match"/> says which command a press is and the application decides when to ask.
/// </summary>
public sealed class TerminalCommands
{
    private readonly Dictionary<string, TerminalCommand> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TerminalCommand> ordered = [];
    private readonly Dictionary<string, string> bound = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every command, in the order they were registered, which is the order a palette lists them in.</summary>
    public IReadOnlyList<TerminalCommand> All => ordered;

    public int Count => ordered.Count;

    /// <summary>What went wrong reading a keys file or binding a key, said by whoever owns these.</summary>
    public Action<string>? OnProblem { get; set; }

    /// <summary>Called when a command came or went or moved key, for menus that write the keys.</summary>
    public Action? OnChanged { get; set; }

    public bool Contains(string name) => byName.ContainsKey(name);

    public TerminalCommand? Find(string name) => byName.TryGetValue(name, out TerminalCommand? command) ? command : null;

    /// <summary>Puts a command in, over one of the same name, on the key the keys files say or the one it asks for.</summary>
    public TerminalCommand Register(string name, string title, Action run, string key = "", object? owner = null)
    {
        if (byName.TryGetValue(name, out TerminalCommand? old))
            ordered.Remove(old);

        var command = new TerminalCommand(name, title, run, owner);
        string wanted = bound.TryGetValue(name, out string? fromFile) ? fromFile : key;
        if (!command.Bind(wanted))
            OnProblem?.Invoke($"{name}: no key called {wanted}, they're written like Ctrl+Shift+K");

        byName[name] = command;
        ordered.Add(command);
        OnChanged?.Invoke();
        return command;
    }

    /// <summary>Takes out every command an owner registered, a plugin switched off or gone wrong.</summary>
    public void Unregister(object owner)
    {
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            if (Equals(ordered[i].Owner, owner))
            {
                byName.Remove(ordered[i].Name);
                ordered.RemoveAt(i);
            }
        }

        OnChanged?.Invoke();
    }

    /// <summary>The key a command is on as the menus write it, empty for none or no such command.</summary>
    public string KeyOf(string name) => byName.TryGetValue(name, out TerminalCommand? command) ? command.Key : string.Empty;

    /// <summary>The command a press is, null for a press no command is on. Allocates nothing, it is asked every key.</summary>
    public TerminalCommand? Match(in UIKeyPress press)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Parsed is { } key && TerminalShortcut.Matches(press, key))
                return ordered[i];
        }

        return null;
    }

    /* Keys files */

    /// <summary>
    /// Reads keys files, each over the ones before it, and puts every command there is on what they say. A file that
    /// isn't there is skipped, one that doesn't read is said and skipped.
    /// </summary>
    public void LoadKeys(params string[] files)
    {
        bound.Clear();
        foreach (string file in files)
        {
            if (!File.Exists(file))
                continue;

            foreach (var (name, key) in ReadKeys(file, OnProblem))
                bound[name] = key;
        }

        foreach (TerminalCommand command in ordered)
        {
            if (bound.TryGetValue(command.Name, out string? key) && !command.Bind(key))
                OnProblem?.Invoke($"{Path.GetFileName(files[^1])}: no key called {key} for {command.Name}");
        }

        OnChanged?.Invoke();
    }

    /// <summary>What a keys file says, a command name and its key, nothing for one that doesn't read.</summary>
    public static List<(string Name, string Key)> ReadKeys(string path, Action<string>? problem = null)
    {
        var read = new List<(string, string)>();
        try
        {
            read.AddRange(ReadKeysText(File.ReadAllText(path)));
        }
        catch (Exception e)
        {
            problem?.Invoke($"{Path.GetFileName(path)} didn't read, {e.Message}");
        }

        return read;
    }

    /// <summary>What the text of a keys file says. Throws when it isn't one.</summary>
    public static List<(string Name, string Key)> ReadKeysText(string text)
    {
        HIDLRuntime runtime = new() { Output = null };
        var (success, message) = runtime.Evaluate(text);
        if (!success || runtime.UserScope.Lookup("keys") is not ObjectValue keys)
            throw new InvalidDataException(success ? "it has to declare an object called 'keys'" : message);

        var read = new List<(string, string)>();
        foreach (var (name, key) in keys.Properties)
        {
            if (key is StringValue written)
                read.Add((name, written.Value));
        }

        return read;
    }

    /// <summary>A keys file of the keys given, in the order given, for whoever writes one out.</summary>
    public static string WriteKeys(IEnumerable<(string Name, string Key)> keys, string about)
    {
        var written = new HIDLObject();
        foreach (var (name, key) in keys)
            written.Add(name, key);
        return new HIDLDocument().Comment(about.TrimEnd()).Let("keys", written).Write();
    }
}
