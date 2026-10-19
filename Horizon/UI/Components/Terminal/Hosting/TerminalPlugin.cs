using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// A plugin of a terminal application, a folder with a plugin.hor in it. The file says what the plugin is
/// (<c>let plugin = { name, description, version, author }</c>), registers its commands as it runs, and has functions
/// the host calls when things happen (<c>on_start(terminal)</c>, <c>on_tabs</c>, <c>on_focus</c>, <c>on_exit(terminal,
/// code)</c>, <c>on_theme(terminal, name)</c>, <c>on_line(terminal, line)</c>), in a runtime of its own with the
/// terminal object the only thing it is handed (<see cref="TerminalHost"/>), under the name <c>terminal</c> and any
/// others the application keeps (Dusk's <c>dusk</c>). Micro's Lua plugins and Hex's extensions are what it is modelled on.
/// </summary>
public sealed class TerminalPlugin
{
    public const string FILE = "plugin.hor";

    /// <summary>The hooks a plugin can have, the functions the host calls by name when something happens.</summary>
    public static readonly (string Name, string Parameters, string About)[] Hooks =
    [
        ("on_start", "terminal", "Once, when the program is up and the first tab is open."),
        ("on_tabs", "terminal", "A tab opened, closed, renamed or another one picked."),
        ("on_focus", "terminal", "The keyboard went to another terminal."),
        ("on_exit", "terminal, code", "A shell ended, with its exit code."),
        ("on_theme", "terminal, name", "The theme changed, with its name."),
        ("on_line", "terminal, line", "A line was run in a shell, with what was typed.")
    ];

    /// <summary>The folder it is in, which is also what the settings know it by.</summary>
    public string Folder { get; }

    public string Id => Path.GetFileName(Folder);

    public string Name { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;

    /// <summary>Whether it came with the program rather than being put in the user's folder.</summary>
    public bool BuiltIn { get; }

    public bool Enabled { get; set; }

    /// <summary>What went wrong with it, null while nothing has. One that went wrong is left alone until it is loaded again.</summary>
    public string? Failed { get; private set; }

    public HIDLRuntime? Runtime { get; private set; }

    /// <summary>When its file was written, as of its last load, so a change on disk loads it again.</summary>
    public DateTime LoadedFrom { get; private set; }

    public TerminalPlugin(string folder, bool builtIn)
    {
        Folder = folder;
        Name = Path.GetFileName(folder);
        BuiltIn = builtIn;
    }

    /// <summary>
    /// Runs its file with the terminal object declared under every name given, and reads what it says it is. False
    /// with <see cref="Failed"/> saying why if it didn't run.
    /// </summary>
    public bool Load(ObjectValue api, IReadOnlyList<string> names, Action<string> printed)
    {
        Failed = null;
        string file = Path.Combine(Folder, FILE);
        LoadedFrom = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
        Runtime = new HIDLRuntime { BaseDirectory = Folder, Output = printed };
        foreach (string name in names)
            Runtime.UserScope.DeclareSystem(name, api);

        if (!Call(() => Runtime.RunFile(file)))
            return false;

        if (Runtime.UserScope.Variables.TryGetValue("plugin", out IRuntimeValue? about) && about is ObjectValue said)
        {
            Name = Text(said, "name") ?? Name;
            Description = Text(said, "description") ?? string.Empty;
            Version = Text(said, "version") ?? string.Empty;
            Author = Text(said, "author") ?? string.Empty;
        }

        return true;
    }

    /// <summary>Whether its file changed on disk since it was loaded.</summary>
    public bool ChangedOnDisk
    {
        get
        {
            string file = Path.Combine(Folder, FILE);
            return File.Exists(file) && File.GetLastWriteTimeUtc(file) != LoadedFrom;
        }
    }

    /// <summary>Lets go of its runtime, for a plugin switched off.</summary>
    public void Unload()
    {
        Runtime = null;
        Failed = null;
    }

    /// <summary>Calls one of its functions if it has one by that name. False if it went wrong, which switches it off.</summary>
    public bool Raise(string name, params IRuntimeValue[] arguments)
    {
        if (Runtime is not { } runtime || Failed is not null || !runtime.UserScope.Variables.TryGetValue(name, out IRuntimeValue? function) || !Values.IsFunction(function))
            return true;

        return Call(() => runtime.Invoke(function, arguments));
    }

    /// <summary>Calls a function it handed over (a command), with what it handed back. False if it went wrong.</summary>
    public bool Invoke(IRuntimeValue function, IRuntimeValue[] arguments, out IRuntimeValue returned)
    {
        returned = Values.Null;
        if (Runtime is not { } runtime || Failed is not null)
            return false;

        IRuntimeValue result = Values.Null;
        bool fine = Call(() => result = runtime.Invoke(function, arguments));
        returned = result;
        return fine;
    }

    /// <summary>
    /// Helper method to run something of the plugin's, for a second at the most, on the program's thread. Nothing it
    /// does gets past this, what went wrong is kept and the plugin is done for.
    /// </summary>
    private bool Call(Action run)
    {
        using var cancel = new CancellationTokenSource(1000);
        Runtime!.Interpreter.Cancellation = cancel.Token;

        try
        {
            run();
            return true;
        }
        catch (Exception e)
        {
            Failed = cancel.IsCancellationRequested ? "it went on for more than a second and was stopped" : e.Message;
            return false;
        }
        finally
        {
            Runtime.Interpreter.Cancellation = CancellationToken.None;
        }
    }

    private static string? Text(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text ? text.Value : null;
}
