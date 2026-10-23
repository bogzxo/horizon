using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

using Key = Silk.NET.Input.Key;

namespace Horizon.UI.Components;

/// <summary>What a program tells a <see cref="TerminalHost"/> about itself.</summary>
public sealed class TerminalHostOptions
{
    /// <summary>Which program this is, "dusk" or "hex", what <c>terminal.host</c> says to a plugin.</summary>
    public string Name { get; init; } = "terminal";

    /// <summary>The program's version, <c>terminal.version</c>.</summary>
    public string Version { get; init; } = "0";

    /// <summary>The names the terminal object is declared under in a plugin, <c>terminal</c> first, Dusk keeps <c>dusk</c> as well.</summary>
    public IReadOnlyList<string> ObjectNames { get; init; } = ["terminal"];

    /// <summary>The folders plugins are found in, the program's own first (built in) and then the user's.</summary>
    public IReadOnlyList<(string Folder, bool BuiltIn)> PluginFolders { get; init; } = [];

    /// <summary>The keys files, read one over the other, the program's own first and the user's last.</summary>
    public IReadOnlyList<string> KeysFiles { get; init; } = [];

    /// <summary>Where the settings are kept, null for settings that are never read or written.</summary>
    public string? SettingsFile { get; init; }

    /// <summary>Where the history of the lines run is kept, null for one that lasts the run.</summary>
    public string? HistoryFile { get; init; }

    /// <summary>Says something in the status bar, an error when the flag says so.</summary>
    public Action<string, bool>? Say { get; init; }

    /// <summary>Puts a card up for something somebody has to read, an error when the flag says so.</summary>
    public Action<string, bool>? Notify { get; init; }

    /// <summary>What the host asks of the program round it, the palette, a list to pick from, a line of text, a file opened.</summary>
    public ITerminalHostUi? Ui { get; init; }

    /// <summary>
    /// The terminal's own files that come with the engine (Assets/terminal), the core and profiles plugins and the
    /// keys they come on. Every program reads these first, under its own.
    /// </summary>
    public const string ENGINE_ASSETS = "Assets/terminal";

    /// <summary>What the program's own functions on the object are, for api.hor, the same names <see cref="Api"/> hands over.</summary>
    public IReadOnlyList<TerminalApiFunction> ApiFunctions { get; init; } = [];

    /// <summary>Where api.hor is written, the description of the program for an editor, null for nowhere (a test).</summary>
    public string? ApiFile { get; init; }

    /// <summary>What the program adds to the terminal object of a plugin, its own functions.</summary>
    public Func<TerminalPlugin, IEnumerable<(string Name, IRuntimeValue Value)>>? Api { get; init; }

    /// <summary>The settings changed (a plugin set one, the file was saved), for what the program applies itself.</summary>
    public Action? SettingsChanged { get; init; }

    /// <summary>A problem a matcher read off a terminal's output, with the terminal it was read off, its file found from the shell's folder when it could be.</summary>
    public Action<TerminalProblem, TerminalView>? Problem { get; init; }

    /// <summary>A line was run in a terminal, what it said was wrong before is old news, for whoever keeps the problems.</summary>
    public Action<TerminalView>? ProblemsCleared { get; init; }

    /// <summary>Whether the profiles of the machine are looked for (off for a test that says what they are).</summary>
    public bool DiscoverProfiles { get; init; } = true;

    /// <summary>
    /// The program an embedded profile runs, null for one the host doesn't know either. Asked first, the host runs
    /// the HIDL REPL for the profile with the id hidl when the program has nothing of its own for it.
    /// </summary>
    public Func<TerminalProfile, ITerminalProgram?>? ProgramFor { get; init; }
}

/// <summary>
/// What a terminal application is, less its window. A <see cref="TerminalPanel"/> and round it the commands by name
/// with their keys (<see cref="TerminalCommands"/>, the keys files over them), the plugins in their runtimes driving the
/// terminal object (<see cref="TerminalPlugin"/>, TerminalHost.Api has the object), the settings file
/// (<see cref="TerminalSettings"/>) put onto the panel, and the history of the lines run on the arrows
/// (<see cref="TerminalHistory"/>). Dusk is a window round one of these and Hex's terminal page is another, so a
/// plugin, a key or a setting means the same in both. The program sees to its own window, menus and palette and adds
/// what it has to the object through <see cref="TerminalHostOptions.Api"/>.
/// <code>
/// var host = new TerminalHost(panel, new TerminalHostOptions { Name = "hex", PluginFolders = [(folder, true)], ... });
/// host.Start();
/// host.Panel.NewTab();
/// // every tick
/// host.Update(dt);
/// </code>
/// </summary>
public sealed partial class TerminalHost : IDisposable
{
    private readonly TerminalHostOptions options;
    private readonly List<TerminalPlugin> plugins = [];
    private readonly Dictionary<TerminalPlugin, ObjectValue> objects = [];

    public TerminalPanel Panel { get; }
    public TerminalCommands Commands { get; } = new();
    public TerminalSettings Settings { get; }
    public TerminalHistory History { get; }
    public TerminalHostOptions Options => options;

    /// <summary>The plugins there are, on and off, in the order they were found.</summary>
    public IReadOnlyList<TerminalPlugin> Plugins => plugins;

    public TerminalHost(TerminalPanel panel, TerminalHostOptions options)
    {
        Panel = panel;
        this.options = options;
        Settings = new TerminalSettings(options.SettingsFile);
        History = new TerminalHistory(options.HistoryFile) { Limit = Settings.HistorySize };
        Commands.OnProblem = problem => Say(problem, true);

        panel.OnKey = KeyPressed;
        panel.CommandOf = profile => Settings.ShellIntegration ? TerminalIntegration.Command(profile) : profile.Command;
        panel.EnvironmentOf = profile => TerminalIntegration.Environment(profile, TerminalIntegration.CommandsFile(profile), Settings.ShellIntegration);
        panel.OnStartProfile = StartProgram;
        panel.OnViewMade += Wire;
        panel.OnFocusChanged += _ => Raise("on_focus");
        panel.OnChanged += () => Raise("on_tabs");
        panel.OnExited = (_, code) =>
        {
            Raise("on_exit", new NumberValue(code));
            return true;
        };
    }

    /// <summary>The id of the HIDL REPL's profile, which every program has.</summary>
    public const string HIDL_PROFILE = "hidl";

    /// <summary>The ways programs say what is wrong, the shipped ones and what plugins add (terminal.problem_matcher).</summary>
    public List<TerminalProblemMatcher> ProblemMatchers { get; } = [.. TerminalProblemMatcher.Shipped];

    /// <summary>A terminal as it is made, what it types followed, what it writes read for problems, its links opened.</summary>
    private void Wire(TerminalView view)
    {
        view.OnInput = bytes => Typed(view, bytes);
        view.AllowClipboard = Settings.ClipboardWrite;
        view.OnTextLink = link => OpenLink(link);
        view.OnLink = target => OpenLink(new TerminalLink(target, 0, 0));
        ColourLine(view);

        var reader = new TerminalProblemReader(ProblemMatchers, problem => Found(view, problem));
        view.OnOutput = bytes => reader.Feed(bytes);
        view.OnRequest = Requested;

        // A program put on the PATH since the last terminal was opened is known to this one's colouring, and to the shells
        // started after it, a compiler installed while the program was up was nowhere for them
        Horizon.Core.Platform.PlatformPaths.RefreshPath();
        TerminalCommandLookup.ScanPath();
    }

    /// <summary>
    /// What a shell asked for with the private OSC of its integration (TerminalScreen.REQUEST_OSC). There is the dawn
    /// command, <c>dawn;inline;path</c> edits a file in place of the pane, <c>dawn;open;path</c> hands a file or a folder to
    /// Dawn, <c>dawn;window;path</c> opens a new Dawn window on it. A folder can't be edited in place, so it goes to Dawn,
    /// and no path at all is the folder the shell is in. And the tui command, <c>tui;where;name</c> starts a program with a
    /// screen of its own in a tab, a "split" or "down".
    /// </summary>
    public void Requested(TerminalView view, string request)
    {
        string[] parts = request.Split(';', 3);
        if (parts.Length == 3 && parts[0] == "tui")
        {
            RunTui(parts[2].Trim().Trim('"'), parts[1]);
            return;
        }

        if (parts.Length < 2 || parts[0] != "dawn")
            return;

        string mode = parts[1];
        string path = parts.Length > 2 ? parts[2].Trim().Trim('"') : string.Empty;

        // The shells say a whole path as Windows has it, the folder a shell is in can be anything (Git Bash says /c/Users)
        string from = view.WorkingDirectory is { } folder && Path.IsPathFullyQualified(folder) ? folder : System.Environment.CurrentDirectory;
        if (path.Length == 0)
            path = from;

        try
        {
            path = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, from);
        }
        catch (Exception)
        {
            Say($"dawn: {path} isn't a path", error: true);
            return;
        }

        if (mode == "inline" && !Directory.Exists(path))
        {
            if (!view.EditInline(path))
                Say($"dawn: {path} can't be read", error: true);
            return;
        }

        bool window = mode == "window";
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Say($"dawn: there's nothing at {path}", error: true);
            return;
        }

        if (options.Ui?.OpenInDawn(path, window) != true)
            Say(window ? "dawn: no Dawn to open a window of" : $"dawn: no Dawn to open {Path.GetFileName(path)} in", error: true);
    }

    /// <summary>The rules of the output's colours, the shipped ones and what plugins add (terminal.output_rule).</summary>
    public TerminalOutputHighlighter OutputRules { get; } = new();

    private readonly Dictionary<string, TerminalCommandLookup> lookups = new(StringComparer.Ordinal);

    /// <summary>
    /// The line typed into a terminal's shell coloured as the settings say, and the list of what finishes a word for the
    /// shells that need one (cmd and bash, PowerShell does its own).
    /// </summary>
    private void ColourLine(TerminalView view)
    {
        TerminalShellLine? line = ShellLineFor(view.Profile);
        view.LineColourer = Settings.HighlightLine && line is not null && !Settings.PlainProfiles.Contains(view.Profile!.Id) ? line : null;
        view.Completer = Settings.Completion && line is { Kind: "cmd" or "bash" or "powershell" } ? CompleterFor(view.Profile!, line) : null;
        view.OutputHighlighter = Settings.HighlightOutput ? OutputRules : null;
        view.CopyOnSelect = Settings.CopyOnSelect;
        view.RightClickPaste = Settings.RightClickPaste;
        view.Bell = Settings.Bell switch { "sound" => TerminalBell.Sound, "none" => TerminalBell.None, _ => TerminalBell.Flash };
    }

    /// <summary>
    /// What completes a shell's line. cmd and bash get commands, files and the tools' words, PowerShell only the tools'
    /// (it does the rest itself), and a WSL shell no tools, its git isn't the one this would ask.
    /// </summary>
    private TerminalShellCompletion CompleterFor(TerminalProfile profile, TerminalShellLine line)
    {
        bool wsl = profile.Id.StartsWith("wsl:", StringComparison.Ordinal);
        TerminalToolCompletion? tools = wsl || Settings.CompletionTools.Count == 0 ? null : new TerminalToolCompletion(Settings.CompletionTools);
        return new TerminalShellCompletion(line, tools, toolsOnly: line.Kind == "powershell");
    }

    /// <summary>The lexer and the commands of a profile's shell, null for a profile that is no shell this knows.</summary>
    public TerminalShellLine? ShellLineFor(TerminalProfile? profile)
    {
        if (profile is null)
            return null;

        string kind = TerminalIntegration.KindOf(profile);
        if (kind.Length == 0)
            return null;

        if (!lookups.TryGetValue(profile.Id, out TerminalCommandLookup? lookup))
            lookups[profile.Id] = lookup = new TerminalCommandLookup(kind, Settings.ShellIntegration ? TerminalIntegration.CommandsFile(profile) : null);
        return new TerminalShellLine(kind, lookup);
    }

    private void Found(TerminalView view, TerminalProblem problem)
    {
        string file = TerminalLinks.Resolve(problem.File, view.WorkingDirectory) ?? problem.File;
        options.Problem?.Invoke(problem with { File = file }, view);
    }

    /// <summary>A link opened, a file in the program's own editor at its line, anything else by the machine.</summary>
    public void OpenLink(TerminalLink link)
    {
        if (link.File is { } file)
        {
            Open(file, link.Line);
            return;
        }

        string target = link.Target;
        if (target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && TerminalScreen.FolderOf(target) is { } path && File.Exists(path))
        {
            Open(path);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Say($"{target} couldn't be opened, {e.Message}", true);
        }
    }

    /// <summary>The profiles found, the plugins started, the keys read and the settings put on the panel. Once.</summary>
    public void Start()
    {
        if (options.DiscoverProfiles)
        {
            foreach (TerminalProfile profile in TerminalProfiles.Discover())
            {
                if (Settings.Wsl || !profile.Id.StartsWith("wsl:", StringComparison.Ordinal))
                    Panel.Profiles.Add(profile);
            }
        }

        if (!Panel.Profiles.Exists(profile => profile.Id == HIDL_PROFILE))
            Panel.Profiles.Add(new TerminalProfile("HIDL", string.Empty, "symbol-namespace", HIDL_PROFILE) { Kind = TerminalProfileKind.Embedded });

        ApplySettings();
        LoadPlugins();
        Commands.LoadKeys([.. options.KeysFiles]);
        WriteApi();
    }

    /// <summary>
    /// The description of the program written into api.hor for an editor, laid over what another program wrote there,
    /// so it says what Dusk and Hex both have. Written only when it changed.
    /// </summary>
    public void WriteApi()
    {
        if (options.ApiFile is not { } path)
            return;

        try
        {
            TerminalApiDescription ours = TerminalApiDescription.Of(this, options.ApiFunctions);
            string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
            TerminalApiDescription written = existing is not null && TerminalApiDescription.Read(existing) is { } before ? before : TerminalApiDescription.Engine();
            written.Settings.Clear();
            written.Settings.AddRange(ours.Settings);
            written.Hooks.Clear();
            written.Hooks.AddRange(ours.Hooks);
            written.Merge(ours);

            string text = written.Write();
            if (text != existing)
                File.WriteAllText(path, text);
        }
        catch (Exception e)
        {
            Say($"api.hor couldn't be written, {e.Message}", true);
        }
    }

    /// <summary>The description of the program as it is now, what an editor in the program itself knows its files by.</summary>
    public TerminalApiDescription Describe()
    {
        TerminalApiDescription described = options.ApiFile is { } path && File.Exists(path) && TerminalApiDescription.Read(File.ReadAllText(path)) is { } read
            ? read
            : TerminalApiDescription.Engine();
        described.Merge(TerminalApiDescription.Of(this, options.ApiFunctions));
        return described;
    }

    public void Say(string message, bool error = false) => options.Say?.Invoke(message, error);

    /// <summary>The engine's own plugins folder and keys file, next to whatever program is running.</summary>
    public static string EnginePlugins => Path.Combine(AppContext.BaseDirectory, TerminalHostOptions.ENGINE_ASSETS, "plugins");

    public static string EngineKeys => Path.Combine(AppContext.BaseDirectory, TerminalHostOptions.ENGINE_ASSETS, "keys.hor");

    /* What the core plugin's commands ask of the program */

    /// <summary>A tab renamed, asked for with the program's own way of asking.</summary>
    public void AskRename(TerminalTab tab) =>
        options.Ui?.AskText("Rename the tab", "What the tab says instead of what the shell calls it, nothing for that.", tab.Renamed ?? string.Empty, text => Panel.Rename(tab, text));

    /// <summary>A shell picked from the profiles and a tab of it opened.</summary>
    public void NewTabWith()
    {
        var profiles = Panel.Profiles.ToArray();
        options.Ui?.Choose("New tab", "Which shell", [.. profiles.Select(profile => $"$({profile.Icon}) {profile.Name}   {profile.Command}")], index => Panel.NewTab(profiles[index]));
    }

    /// <summary>Every command with its key in a list, a pick runs it.</summary>
    public void ListKeys()
    {
        var commands = Commands.All.OrderBy(command => command.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        options.Ui?.Choose("Keyboard shortcuts", "Every command with its key, a pick runs it, keys.hor changes them",
            [.. commands.Select(command => $"{command.Title}   ({(command.Key.Length > 0 ? command.Key : "no key")})   {command.Name}")], index => Run(commands[index].Name));
    }

    /// <summary>Every plugin with whether it is on, a pick switches it.</summary>
    public void ListPlugins()
    {
        var listed = plugins.ToArray();
        options.Ui?.Choose("Plugins", "The plugins there are, pick one to switch it on or off",
            [.. listed.Select(plugin => $"$({(plugin.BuiltIn ? "lock" : plugin.Enabled ? "check" : "circle-large-outline")}) {plugin.Name} {plugin.Version}   {plugin.Description}   ({(plugin.Failed is not null ? $"stopped, {plugin.Failed}" : plugin.BuiltIn ? "comes with it, always on" : plugin.Enabled ? "on" : "off")})")],
            index => SwitchPlugin(listed[index], !listed[index].Enabled));
    }

    /// <summary>settings.hor opened to edit, written first when it isn't there yet.</summary>
    public void OpenSettings()
    {
        if (Settings.Path is not { } path)
        {
            Say("the settings aren't kept in a file this time", true);
            return;
        }

        if (!File.Exists(path))
            SaveSettings();
        Open(path);
    }

    /// <summary>The user's keys.hor opened to edit, made with a line of help first when it isn't there yet.</summary>
    public void OpenKeys()
    {
        if (options.KeysFiles.Count == 0)
            return;

        string path = options.KeysFiles[^1];
        if (!File.Exists(path))
        {
            try
            {
                File.WriteAllText(path, new HIDLDocument()
                    .Comment("Your own keys, a command by its name and the key it goes on, \"\" for none, over the ones that come with the program.\n" +
                        "Keyboard shortcuts lists every command with its name. Taken in the moment it is saved.")
                    .Define("dusk")
                    .Let("keys", new HIDLObject().Note("new_tab: \"Ctrl+T\","))
                    .Write());
            }
            catch (Exception e)
            {
                Say($"{path} couldn't be made, {e.Message}", true);
                return;
            }
        }

        Open(path);
    }

    /// <summary>A file opened in whatever the program opens files in, said when it can't be.</summary>
    public void Open(string path, int line = 0)
    {
        if (options.Ui?.OpenFile(path, line) != true)
            Say($"{Path.GetFileName(path)} couldn't be opened", true);
    }

    private const float ZOOM_STEP = 1.1f, MIN_TEXT = 0.08f, MAX_TEXT = 0.5f, TEXT_AS_IT_COMES = 0.16f;

    /// <summary>The terminals' text bigger or smaller by so many steps, 0 for the size it comes at, and kept.</summary>
    public void Zoom(float steps)
    {
        float scale = steps == 0.0f ? TEXT_AS_IT_COMES : Math.Clamp(Settings.TextScale * MathF.Pow(ZOOM_STEP, steps), MIN_TEXT, MAX_TEXT);
        Settings.TextScale = scale;
        ApplySettings();
        SaveSettings();
        Say($"the text is at {scale / TEXT_AS_IT_COMES * 100.0f:0}%");
    }

    /// <summary>An embedded profile started in a view, the program's own program for it or the HIDL REPL. False for one nobody knows.</summary>
    private bool StartProgram(TerminalProfile profile, TerminalView view)
    {
        ITerminalProgram? program = options.ProgramFor?.Invoke(profile) ?? TuiFor(profile);
        if (program is null && profile.Id == HIDL_PROFILE)
            program = new TerminalHidlRepl(options.HistoryFile is { } history ? Path.Combine(Path.GetDirectoryName(history) ?? ".", "hidl_" + Path.GetFileName(history)) : null);
        if (program is null)
            return false;

        view.Start(program, Panel.WorkingDirectory);
        return true;
    }

    public void Notify(string message, bool error = false) => (options.Notify ?? options.Say)?.Invoke(message, error);

    /// <summary>A profile by its id or its name, null for none or an empty text.</summary>
    public TerminalProfile? ProfileCalled(string id)
    {
        if (id.Length == 0)
            return null;

        foreach (TerminalProfile profile in Panel.Profiles)
        {
            if (profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || profile.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
                return profile;
        }

        return null;
    }

    /// <summary>The profile a new tab gets, the settings' one or the first there is.</summary>
    public TerminalProfile? UsualProfile => ProfileCalled(Settings.Profile) ?? Panel.Profiles.FirstOrDefault();

    /// <summary>Runs a command by name, false when there is none by it. What it does going wrong is said and not thrown.</summary>
    public bool Run(string name)
    {
        if (Commands.Find(name) is not { } command)
        {
            Say($"nothing called {name}", true);
            return false;
        }

        try
        {
            command.Run();
        }
        catch (Exception e)
        {
            Say($"{name}: {e.Message}", true);
        }

        return true;
    }

    /// <summary>Whether the program is busy with something keys shouldn't reach past (a dialog), asked before every key.</summary>
    public Func<bool>? Blocked { get; set; }

    /// <summary>A key pressed in a terminal, before the shell sees it. True when the history or a command took it.</summary>
    public bool KeyPressed(UIKeyPress press)
    {
        if (Blocked?.Invoke() == true)
            return false;

        if (HistoryKey(press))
            return true;

        if (Commands.Match(press) is { } command)
        {
            Run(command.Name);
            return true;
        }

        return false;
    }

    /* Settings */

    /// <summary>The settings put onto the panel and every terminal there is, and the program told.</summary>
    public void ApplySettings()
    {
        Panel.AnimationTime = Settings.Animations ? Settings.AnimationTime : 0.0f;
        Panel.CursorGlide = Settings.CursorGlide;
        Panel.Scrollback = Settings.Scrollback;
        Panel.CloseOnExit = Settings.CloseOnExit;
        Panel.TextScale = Settings.TextScale;
        History.Limit = Settings.HistorySize;
        foreach (TerminalTab tab in Panel.Tabs)
        {
            foreach (TerminalView view in tab.Views)
            {
                view.TextScale = Settings.TextScale;
                view.Animated = Panel.Animated;
                view.CursorGlide = Settings.CursorGlide;
                view.Scrollback = Settings.Scrollback;
                view.AllowClipboard = Settings.ClipboardWrite;
                ColourLine(view);
            }
        }

        options.SettingsChanged?.Invoke();
    }

    public void SaveSettings()
    {
        // What was written is kept, so the watcher seeing the file change doesn't take the program's own save back in
        writtenSettings = Settings.Write();
        if (Settings.Save() is { } problem)
            Say(problem, true);
    }

    private string? writtenSettings;

    /* Plugins */

    private void LoadPlugins()
    {
        foreach (var (folder, builtIn) in options.PluginFolders)
        {
            if (!Directory.Exists(folder))
                continue;

            foreach (string inside in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(inside, TerminalPlugin.FILE)))
                    continue;

                // A plugin of the user's by the name of a built in one is that one, changed
                string id = Path.GetFileName(inside);
                plugins.RemoveAll(plugin => plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                // the ones the terminal comes with are always on, off they took the tabs, the keys and the palette with them
                plugins.Add(new TerminalPlugin(inside, builtIn) { Enabled = builtIn || !Settings.DisabledPlugins.Contains(id) });
            }
        }

        foreach (TerminalPlugin plugin in plugins)
        {
            if (plugin.Enabled)
                StartPlugin(plugin);
        }
    }

    /// <summary>Loads a plugin, its file registers its commands as it runs, or says why that didn't work and leaves it be.</summary>
    public void StartPlugin(TerminalPlugin plugin)
    {
        Commands.Unregister(plugin);
        ticking.RemoveAll(pair => pair.Plugin == plugin);
        ObjectValue api = ObjectOf(plugin);
        bool loaded = plugin.Load(api, options.ObjectNames, line => Console.WriteLine($"[{plugin.Name}] {line}"));
        if (!loaded)
            PluginFailed(plugin);
    }

    /// <summary>Tells every plugin that is on that something happened, by calling its function of that name if it has one.</summary>
    public void Raise(string hook, params IRuntimeValue[] arguments)
    {
        for (int i = 0; i < plugins.Count; i++)
        {
            TerminalPlugin plugin = plugins[i];
            if (!plugin.Enabled || plugin.Runtime is null || plugin.Failed is not null)
                continue;

            if (!plugin.Raise(hook, [ObjectOf(plugin), .. arguments]))
                PluginFailed(plugin);
        }
    }

    private void PluginFailed(TerminalPlugin plugin)
    {
        Commands.Unregister(plugin);
        Notify($"the plugin {plugin.Name} stopped, {plugin.Failed ?? "?"}", true);
    }

    /// <summary>Switches a plugin on (it starts straight away) or off (its commands go), and keeps that in the settings.</summary>
    public void SwitchPlugin(TerminalPlugin plugin, bool on)
    {
        // a plugin the terminal comes with is the terminal, switched off nothing works, so it isn't
        if (plugin.BuiltIn && !on)
        {
            Say($"{plugin.Name} comes with the terminal and stays on");
            return;
        }

        plugin.Enabled = on;
        if (on)
            Settings.DisabledPlugins.Remove(plugin.Id);
        else
            Settings.DisabledPlugins.Add(plugin.Id);
        SaveSettings();

        if (on)
        {
            StartPlugin(plugin);
        }
        else
        {
            Commands.Unregister(plugin);
            ticking.RemoveAll(pair => pair.Plugin == plugin);
            plugin.Unload();
        }
    }

    // What plugins asked to be told every tick
    private readonly List<(TerminalPlugin Plugin, IRuntimeValue Function)> ticking = [];
    private (TerminalPlugin Plugin, IRuntimeValue Function)[] tickingNow = [];

    /// <summary>Every tick. The plugins that asked to be told, and the files that changed taken in.</summary>
    public void Update(float dt)
    {
        TakeChanges();

        if (ticking.Count > 0)
            TickPlugins(dt);
    }

    // The lambdas that take a plugin's functions away are in a method of their own, a method that runs every tick and
    // has a lambda using one of its locals makes an object for it every tick whether the lambda is reached or not
    private void TickPlugins(float dt)
    {
        if (tickingNow.Length != ticking.Count)
            tickingNow = new (TerminalPlugin, IRuntimeValue)[ticking.Count];
        ticking.CopyTo(tickingNow);

        IRuntimeValue elapsed = new NumberValue(dt);
        for (int i = 0; i < tickingNow.Length; i++)
        {
            var (plugin, function) = tickingNow[i];
            if (!plugin.Enabled || plugin.Failed is not null)
                continue;

            tickArguments[0] = ObjectOf(plugin);
            tickArguments[1] = elapsed;
            if (!plugin.Invoke(function, tickArguments, out _))
                StopTicking(plugin);
        }
    }

    // What on_tick is handed, the plugin's object and the seconds since the last tick, the one array every tick
    private readonly IRuntimeValue[] tickArguments = new IRuntimeValue[2];

    // A plugin whose on_tick failed, out of the ticking. Apart from TickPlugins so its lambda isn't made every tick
    private void StopTicking(TerminalPlugin plugin)
    {
        ticking.RemoveAll(pair => pair.Plugin == plugin);
        PluginFailed(plugin);
    }

    /* The history on the arrows */

    // What has been typed into each shell since its last Return, and where it is in the history
    private sealed class LineState
    {
        public readonly StringBuilder Typed = new();
        public readonly TerminalHistory.Cursor Cursor = new();
    }

    private readonly Dictionary<TerminalView, LineState> lines = [];

    private LineState StateOf(TerminalView view)
    {
        if (!lines.TryGetValue(view, out LineState? state))
            lines[view] = state = new LineState();
        return state;
    }

    /// <summary>What has been typed into a shell since its last Return, as far as the host could follow it.</summary>
    public string TypedIn(TerminalView view) => StateOf(view).Typed.ToString();

    /// <summary>What a key sent to a shell, kept as the line being typed, Return putting it in the history.</summary>
    private void Typed(TerminalView view, string bytes)
    {
        if (bytes.Length == 0)
            return;

        // Somebody typing is a shell at its prompt, all of it drawn, the marks that wait are where they belong now
        view.Screen.SettleMarks();
        Broadcast(view, bytes);

        LineState state = StateOf(view);
        switch (bytes)
        {
            case "\r" or "\n":
                // The line as the shell has it, the keys alone lost an arrow back into the middle or a Tab completing a
                // word, and the history had the words out of order. They're kept for a shell whose prompt can't be found
                string line = view.TypedLine() ?? state.Typed.ToString();
                view.Screen.GuessPrompt();
                options.ProblemsCleared?.Invoke(view);
                History.Add(line);
                state.Typed.Clear();
                state.Cursor.Reset();
                Raise("on_line", new StringValue(line.Trim()));
                return;

            case "\u007f" or "\b":
                if (state.Typed.Length > 0)
                    state.Typed.Length--;
                return;
        }

        // An escape sequence is an arrow or a function key, which moves the shell's caret or does something the host
        // can't follow, a control character is the shell's own, so the line stays what it was typed as
        if (bytes[0] == '\u001b')
        {
            state.Cursor.Reset();
            return;
        }

        if (bytes[0] < ' ')
        {
            // Control with C or U throws the line away in every shell
            if (bytes is "\u0003" or "\u0015")
                state.Typed.Clear();
            return;
        }

        state.Typed.Append(bytes);
        state.Cursor.Reset();
    }

    /// <summary>Up or Down in a shell, the line before or after put on the prompt. False when the key is the shell's.</summary>
    private bool HistoryKey(in UIKeyPress press)
    {
        if (!Settings.History || press.Control || press.Shift || press.Alt || press.Key is not (Key.Up or Key.Down))
            return false;

        if (Panel.FocusedView is not { Screen.AlternateScreen: false, Process.IsRunning: true } view || !AtShellPrompt(view.Screen))
            return false;

        // The marks can say prompt while a program runs (they settle late, cmd has no mark for a command starting, and
        // Up and Down went to the history in micro and a TUI in a Dusk opened from Dawn), the system can't, a shell
        // with a program of its own going isn't at its prompt
        if (view.Process?.RunsAProgram == true)
            return false;

        LineState state = StateOf(view);
        string typing = view.TypedLine() ?? state.Typed.ToString();
        if (History.Step(state.Cursor, press.Key == Key.Up ? -1 : 1, typing) is not { } line)
            return true;

        // To the end of the line, as many backspaces as it has characters, then the line, through the shell's own line
        // editing. From wherever the caret was the backspaces left the end of the old line in front of the new one
        var replace = new StringBuilder();
        if (typing.Length > 0)
        {
            replace.Append(TerminalKeys.Key(Key.End, TerminalKeys.Modifiers.None, view.Screen.ApplicationCursorKeys, view.Screen.KeyboardFlags));
            replace.Append('\u007f', typing.EnumerateRunes().Count());
        }
        replace.Append(line);
        view.Send(replace.ToString());
        state.Typed.Clear().Append(line);
        return true;
    }

    /// <summary>
    /// Whether what has the terminal is the shell at its prompt, where Up and Down are the history's, rather than a
    /// program the shell ran (micro, vim, less). ConPTY doesn't always pass a program's switch to the alternate screen on,
    /// so micro looked like the shell and every arrow put an old line in place of the one being edited. With the shell's
    /// marks it's the prompt they say, without them a program that asked for application cursor keys has the arrows.
    /// </summary>
    public static bool AtShellPrompt(TerminalScreen screen) =>
        !screen.AlternateScreen && (screen.MarksSeen ? screen.AtPrompt : !screen.ApplicationCursorKeys);

    /// <summary>The history forgotten, the file with it.</summary>
    public void ClearHistory()
    {
        History.Clear();
        foreach (LineState state in lines.Values)
            state.Cursor.Reset();
    }
}
