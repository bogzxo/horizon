using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>One function of the terminal object as an editor describes it, its name, what it takes and a line of help.</summary>
public sealed record TerminalApiFunction(string Name, string Parameters, string Help);

// The terminal object a plugin is handed, the part every program has. The program adds its own through
// TerminalHostOptions.Api (Dusk its window's, the palette, the themes, quitting, Hex its editor's). One is made for a
// plugin and kept, so the functions that need to know which plugin is asking (a command, every_tick) know it.
public sealed partial class TerminalHost
{
    /// <summary>Every function the terminal object has whatever program it is in, for api.hor and an editor's completion.</summary>
    public static readonly TerminalApiFunction[] CoreApi =
    [
        new("command", "name, title, func(terminal), key", "Registers a command, in the palette and on its key (\"Ctrl+Shift+T\", \"\" for none). keys.hor can move it."),
        new("run", "name", "Runs any command by name, true when there was one."),
        new("commands", "", "The names of every command there is."),
        new("key_of", "name", "The key a command is on, \"\" for none."),
        new("new_tab", "profile?, folder?", "Opens a tab of a profile (its id), the usual one when none is given, and says which tab it is."),
        new("close_tab", "index?", "Closes a tab, the open one when none is given."),
        new("next_tab", "", "The tab after the open one, round to the first."),
        new("previous_tab", "", "The tab before the open one, round to the last."),
        new("select_tab", "index", "Opens a tab by its place, 0 for the first."),
        new("rename_tab", "title", "Renames the open tab, \"\" for the title its shell gives it."),
        new("move_tab", "index", "Moves the open tab to another place in the strip."),
        new("tab", "", "Which tab is open, 0 for the first, -1 for none."),
        new("tabs", "", "The titles of every tab."),
        new("split", "\"right\" or \"down\", profile?", "Splits the focused pane, a new shell on its right or under it."),
        new("close_pane", "", "Closes the focused pane, a tab's last closes the tab."),
        new("focus", "\"left\", \"right\", \"up\" or \"down\"", "Moves the keyboard to the pane next to the focused one, true when there was one."),
        new("panes", "", "How many panes the open tab has."),
        new("zoom_pane", "", "The focused pane over the whole of its tab and back."),
        new("step_tab", "by", "The open tab moved along the strip by so many places, -1 a step to the left."),
        new("swap_pane", "direction", "The focused pane swapped with the one next to it, left, right, up or down, false when there is none."),
        new("rename_pane", "name?", "Renames the focused pane, asks for the name when none is given, \"\" for the program's title."),
        new("pane_title", "", "What the focused pane is called."),
        new("tui", "name, where?", "Runs a program with a screen of its own written in HIDL (files, git, processes, settings, repl, or one of the user's tui folder, or a path), in a new tab, \"split\" or \"down\"."),
        new("tuis", "", "The names of the programs with a screen of their own there are."),
        new("write", "text", "Types text into the focused shell as if it was typed."),
        new("send", "line", "Types a line into the focused shell and presses Return."),
        new("feed", "text", "Writes text onto the focused terminal's screen as if its program had, colours and all."),
        new("screen", "", "The text on the focused terminal's screen."),
        new("selection", "", "What is selected in the focused terminal, \"\" for nothing."),
        new("copy", "", "Copies what is selected in the focused terminal."),
        new("title", "", "What the focused terminal's program called it."),
        new("columns", "", "How many columns the focused terminal has."),
        new("rows", "", "How many rows the focused terminal has."),
        new("running", "", "Whether the focused terminal's shell is still going."),
        new("folder", "", "The folder the focused terminal's shell is in, as far as it has said."),
        new("problem_matcher", "name, pattern, place?", "A way a program says what is wrong, a regex with groups file, line, column, kind and message (and a second for the place of a two line one), read off every terminal."),
        new("output_rule", "name, pattern, colour, underline?", "Colours what programs write where it matches a regex and they left it plain, red, green, yellow, blue, magenta, cyan or dim (highlight_output in settings.hor turns the lot on)."),
        new("history", "", "The lines run in every shell, the oldest first."),
        new("clear_history", "", "Forgets the history, the file with it."),
        new("profiles", "", "The ids of the profiles there are."),
        new("profile_name", "id", "What a profile is called."),
        new("profile", "id, name, command, icon?", "Adds a profile, a shell to open tabs of."),
        new("default_profile", "id?", "The profile a new tab opens, set when one is given."),
        new("current_profile", "", "The id of the focused terminal's profile."),
        new("palette", "", "The command palette."),
        new("choose", "title, [texts], func(index)", "Asks for one of a list, calls the function with the place of the one picked."),
        new("ask", "title, message, text, func(text)", "Asks for a line of text, starting from one, calls the function with what was given."),
        new("ask_rename", "", "Asks for a new name for the open tab."),
        new("new_tab_with", "", "Asks which shell and opens a tab of it."),
        new("keys", "", "Every command with its key, in a list, a pick runs it."),
        new("plugins", "", "Every plugin, a pick switches it on or off."),
        new("open_settings", "", "Opens settings.hor to edit, taken in as it is saved."),
        new("open_keys", "", "Opens the user's keys.hor to edit, taken in as it is saved."),
        new("open", "path, line?", "Opens a file to edit at a line, in Hex or the editor Hex is."),
        new("edit_plugin", "", "Opens the plugin that asks to edit, started again as it is saved."),
        new("zoom", "steps", "The terminals' text bigger by so many steps, smaller by fewer than none, 0 for the size it comes at."),
        new("say", "text", "Says something in the status bar."),
        new("notify", "text, error?", "Puts a card up in the corner."),
        new("setting", "key", "A setting of settings.hor by its key."),
        new("set_setting", "key, value", "Sets a setting and keeps it in settings.hor."),
        new("every_tick", "func(terminal, seconds)", "Calls a function every tick of the program with how long the tick was."),
        new("plugin_folder", "", "The folder of the plugin that asks."),
        new("host", "", "Which program this is, \"dusk\" or \"hex\"."),
        new("version", "", "The program's version."),
        new("platform", "", "\"windows\", \"mac\" or \"linux\".")
    ];

    /// <summary>The names on the object every program has, <see cref="CoreApi"/>'s and the blocks'.</summary>
    public static IEnumerable<string> CoreApiNames => CoreFunctions.Select(function => function.Name);

    /// <summary>Every function the object has whatever program it is in, the core's and the blocks', described.</summary>
    public static IEnumerable<TerminalApiFunction> CoreFunctions => CoreApi.Concat(BlocksFunctions);

    /// <summary>The terminal object of a plugin, made the first time it is asked for and kept.</summary>
    public ObjectValue ObjectOf(TerminalPlugin plugin)
    {
        if (!objects.TryGetValue(plugin, out ObjectValue api))
        {
            var members = new List<(string, IRuntimeValue)>(Core(plugin));
            members.AddRange(BlocksApi());
            if (options.Api?.Invoke(plugin) is { } more)
                members.AddRange(more);
            objects[plugin] = api = Natives.Object([.. members]);
        }

        return api;
    }

    /// <summary>The terminal object of any plugin that is loaded, for whoever wants the names it has (a self test).</summary>
    public ObjectValue? AnyObject => plugins.Find(plugin => plugin.Runtime is not null) is { } any ? ObjectOf(any) : null;

    private TerminalTab? TabAt(int index) => index >= 0 && index < Panel.Tabs.Count ? Panel.Tabs[index] : null;

    private (string, IRuntimeValue)[] Core(TerminalPlugin plugin) =>
    [
        /* Commands */
        ("command", Natives.Action("command", args =>
        {
            string name = args.Text(0);
            string title = args.Text(1);
            IRuntimeValue function = args.Callable(2);
            string key = args.Text(3, string.Empty);
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
                throw args.Wrong("a command's name is one word, like new_tab.");

            Commands.Register(name, title.Length > 0 ? title : name, () =>
            {
                if (!plugin.Invoke(function, [ObjectOf(plugin)], out _))
                    PluginFailed(plugin);
            }, key, plugin);
        })),
        ("run", Natives.Function("run", args => Values.Bool(Run(args.Text(0))))),
        ("commands", Natives.Function("commands", _ => Natives.List(Commands.All.Select(command => command.Name)))),
        ("key_of", Natives.Function("key_of", args => new StringValue(Commands.KeyOf(args.Text(0))))),

        /* Tabs */
        ("new_tab", Natives.Function("new_tab", args =>
        {
            TerminalProfile? profile = args.Has(0) ? ProfileCalled(args.Text(0)) ?? throw args.Wrong($"no profile called {args.Text(0)}.") : UsualProfile;
            Panel.NewTab(profile, args.Has(1) ? args.Text(1) : null);
            return new NumberValue(Panel.CurrentIndex);
        })),
        ("close_tab", Natives.Action("close_tab", args =>
        {
            TerminalTab? tab = args.Has(0) ? TabAt((int)args.Number(0)) : Panel.Current;
            if (tab is not null) Panel.CloseTab(tab);
        })),
        ("next_tab", Natives.Action("next_tab", _ => Panel.NextTab())),
        ("previous_tab", Natives.Action("previous_tab", _ => Panel.PreviousTab())),
        ("select_tab", Natives.Action("select_tab", args => Panel.Select((int)args.Number(0)))),
        ("rename_tab", Natives.Action("rename_tab", args =>
        {
            if (Panel.Current is { } tab) Panel.Rename(tab, args.Text(0, string.Empty));
        })),
        ("move_tab", Natives.Action("move_tab", args =>
        {
            if (Panel.Current is { } tab) Panel.MoveTab(tab, (int)args.Number(0));
        })),
        ("tab", Natives.Function("tab", _ => new NumberValue(Panel.CurrentIndex))),
        ("tabs", Natives.Function("tabs", _ => Natives.List(Panel.Tabs.Select(tab => tab.Title)))),

        /* Panes */
        ("split", Natives.Function("split", args =>
        {
            SplitOrientation orientation = args.Text(0, "right") switch
            {
                "right" or "horizontal" => SplitOrientation.Horizontal,
                "down" or "vertical" => SplitOrientation.Vertical,
                var other => throw args.Wrong($"split right or down, not {other}.")
            };
            TerminalProfile? profile = args.Has(1) ? ProfileCalled(args.Text(1)) : null;
            return Values.Bool(Panel.Split(orientation, profile) is not null);
        })),
        ("close_pane", Natives.Action("close_pane", _ => Panel.ClosePane())),
        ("focus", Natives.Function("focus", args =>
        {
            SplitDirection direction = args.Text(0) switch
            {
                "left" => SplitDirection.Left,
                "right" => SplitDirection.Right,
                "up" => SplitDirection.Up,
                "down" => SplitDirection.Down,
                var other => throw args.Wrong($"focus left, right, up or down, not {other}.")
            };
            return Values.Bool(Panel.FocusNeighbour(direction));
        })),
        ("panes", Natives.Function("panes", _ => new NumberValue(Panel.Current?.Tree.Count ?? 0))),
        ("zoom_pane", Natives.Action("zoom_pane", _ => Panel.ToggleZoom())),
        ("step_tab", Natives.Action("step_tab", args => Panel.MoveCurrent(args.Integer(0)))),
        ("swap_pane", Natives.Function("swap_pane", args => Values.Bool(Panel.SwapNeighbour(DirectionOf(args.Text(0)) ?? throw args.Wrong("left, right, up or down"))))),
        ("rename_pane", Natives.Action("rename_pane", args =>
        {
            if (Panel.FocusedView is not { } view)
                return;
            if (args.Has(0))
                Panel.RenamePane(view, args.Text(0));
            else
                options.Ui?.AskText("Rename the pane", "What the pane says in its corner instead of what the shell calls it, nothing for that.", view.PaneName ?? string.Empty, text => Panel.RenamePane(view, text));
        })),
        ("pane_title", Natives.Function("pane_title", _ => new StringValue(Panel.FocusedView?.PaneTitle ?? string.Empty))),
        ("tui", Natives.Function("tui", args => Values.Bool(RunTui(args.Text(0), args.Has(1) ? args.Text(1) : "tab")))),
        ("tuis", Natives.Function("tuis", _ => Natives.List(Tuis()))),

        /* The focused terminal */
        ("write", Natives.Action("write", args => Panel.FocusedView?.Send(args.Text(0)))),
        ("send", Natives.Action("send", args => Panel.FocusedView?.Send(args.Text(0) + "\r"))),
        ("feed", Natives.Action("feed", args => Panel.FocusedView?.Feed(args.Text(0)))),
        ("screen", Natives.Function("screen", _ => new StringValue(Panel.FocusedView?.Screen.Text() ?? string.Empty))),
        ("selection", Natives.Function("selection", _ => new StringValue(Panel.FocusedView?.SelectedText ?? string.Empty))),
        ("copy", Natives.Action("copy", _ =>
        {
            if (Panel.FocusedView is { HasSelection: true } view)
                UIClipboard.Copy(view.SelectedText);
        })),
        ("title", Natives.Function("title", _ => new StringValue(Panel.FocusedView?.Title ?? string.Empty))),
        ("columns", Natives.Function("columns", _ => new NumberValue(Panel.FocusedView?.Columns ?? 0))),
        ("rows", Natives.Function("rows", _ => new NumberValue(Panel.FocusedView?.Rows ?? 0))),
        ("running", Natives.Function("running", _ => Values.Bool(Panel.FocusedView?.Process is { IsRunning: true }))),
        ("folder", Natives.Function("folder", _ => new StringValue(Panel.FocusedView?.WorkingDirectory ?? string.Empty))),

        ("output_rule", Natives.Action("output_rule", args =>
        {
            TerminalOutputColour colour = TerminalOutputRule.ColourOf(args.Text(2));
            if (colour == TerminalOutputColour.None)
                throw args.Wrong($"'{args.Text(2)}' is no colour, red, green, yellow, blue, magenta, cyan or dim");
            try
            {
                OutputRules.Add(new TerminalOutputRule(args.Text(0), args.Text(1), colour, args.Bool(3, false)));
            }
            catch (ArgumentException e)
            {
                throw args.Wrong($"that pattern doesn't read, {e.Message}");
            }
        })),
        ("problem_matcher", Natives.Action("problem_matcher", args =>
        {
            string name = args.Text(0);
            try
            {
                ProblemMatchers.RemoveAll(matcher => matcher.Name == name);
                ProblemMatchers.Add(new TerminalProblemMatcher(name, args.Text(1), args.Has(2) ? args.Text(2) : null));
            }
            catch (ArgumentException e)
            {
                throw args.Wrong($"that pattern doesn't read, {e.Message}");
            }
        })),

        /* The history */
        ("history", Natives.Function("history", _ => Natives.List(History.Lines))),
        ("clear_history", Natives.Action("clear_history", _ => ClearHistory())),

        /* Profiles */
        ("profiles", Natives.Function("profiles", _ => Natives.List(Panel.Profiles.Select(profile => profile.Id)))),
        ("profile_name", Natives.Function("profile_name", args => new StringValue(ProfileCalled(args.Text(0))?.Name ?? string.Empty))),
        ("profile", Natives.Action("profile", args =>
        {
            string id = args.Text(0), name = args.Text(1), command = args.Text(2);
            Panel.Profiles.RemoveAll(profile => profile.Id == id);
            Panel.Profiles.Add(new TerminalProfile(name, command, args.Text(3, "terminal"), id) { Tag = plugin });
        })),
        ("default_profile", Natives.Function("default_profile", args =>
        {
            if (args.Has(0))
            {
                TerminalProfile picked = ProfileCalled(args.Text(0)) ?? throw args.Wrong($"no profile called {args.Text(0)}.");
                Panel.DefaultProfile = picked;
                Settings.Profile = picked.Id;
                SaveSettings();
            }

            return new StringValue(UsualProfile?.Id ?? string.Empty);
        })),
        ("current_profile", Natives.Function("current_profile", _ => new StringValue(Panel.FocusedView?.Profile?.Id ?? string.Empty))),

        /* What the program is asked */
        ("palette", Natives.Action("palette", _ => options.Ui?.Palette())),
        ("choose", Natives.Action("choose", args =>
        {
            string title = args.Text(0);
            if (args[1] is not ListValue texts)
                throw args.Wrong("choose wants a title, a list of texts and a function of the place of the one picked.");
            IRuntimeValue picked = args.Callable(2);
            string[] items = [.. texts.Items.Select(item => item is StringValue text ? text.Value : Values.Text(item))];
            options.Ui?.Choose(title, string.Empty, items, index =>
            {
                if (!plugin.Invoke(picked, [new NumberValue(index)], out _))
                    PluginFailed(plugin);
            });
        })),
        ("ask", Natives.Action("ask", args =>
        {
            IRuntimeValue answered = args.Callable(3);
            options.Ui?.AskText(args.Text(0), args.Text(1, string.Empty), args.Text(2, string.Empty), text =>
            {
                if (!plugin.Invoke(answered, [new StringValue(text)], out _))
                    PluginFailed(plugin);
            });
        })),
        ("ask_rename", Natives.Action("ask_rename", _ =>
        {
            if (Panel.Current is { } tab) AskRename(tab);
        })),
        ("new_tab_with", Natives.Action("new_tab_with", _ => NewTabWith())),
        ("keys", Natives.Action("keys", _ => ListKeys())),
        ("plugins", Natives.Action("plugins", _ => ListPlugins())),
        ("open_settings", Natives.Action("open_settings", _ => OpenSettings())),
        ("open_keys", Natives.Action("open_keys", _ => OpenKeys())),
        ("open", Natives.Action("open", args => Open(args.Text(0), (int)args.Number(1, 0)))),
        ("edit_plugin", Natives.Action("edit_plugin", _ => Open(Path.Combine(plugin.Folder, TerminalPlugin.FILE)))),
        ("zoom", Natives.Action("zoom", args => Zoom(args.Number(0, 0.0f)))),

        /* Talking */
        ("say", Natives.Action("say", args => Say(args.Text(0, string.Empty)))),
        ("notify", Natives.Action("notify", args => Notify(args.Text(0, string.Empty), args.Bool(1, false)))),

        /* Settings */
        ("setting", Natives.Function("setting", args => Settings.Get(args.Text(0)) ?? throw args.Wrong($"no setting called {args.Text(0)}."))),
        ("set_setting", Natives.Action("set_setting", args =>
        {
            if (!Settings.Set(args.Text(0), args[1]))
                throw args.Wrong($"no setting called {args.Text(0)}.");
            ApplySettings();
            SaveSettings();
        })),
        ("every_tick", Natives.Action("every_tick", args => ticking.Add((plugin, args.Callable(0))))),

        /* What this is */
        ("plugin_folder", new StringValue(plugin.Folder)),
        ("host", new StringValue(options.Name)),
        ("version", new StringValue(options.Version)),
        ("platform", new StringValue(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux"))
    ];
}
