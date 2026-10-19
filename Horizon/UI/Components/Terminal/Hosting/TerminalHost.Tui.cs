using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

// Programs with a screen of their own (TerminalTui) started by name in a tab or a split, out of the user's tui folder or
// the ones the terminal comes with (Assets/terminal/tui), and what they get to ask of the terminal, a file opened, a
// line typed into the shell they were started from, the settings, and somewhere to run HIDL.
public sealed partial class TerminalHost
{
    /// <summary>The folder a user's own programs go in, before the ones that come with the terminal.</summary>
    public static string UserTuiFolder => Path.Combine(TerminalFiles.UserFolder, "tui");

    /// <summary>The folder of the programs that come with the terminal.</summary>
    public static string EngineTuiFolder => Path.Combine(AppContext.BaseDirectory, TerminalHostOptions.ENGINE_ASSETS, "tui");

    /// <summary>The programs there are by name, the user's going over the shipped ones, the widgets left out.</summary>
    public IEnumerable<string> Tuis()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { UserTuiFolder, EngineTuiFolder })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.hor"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name != "widgets")
                    names.Add(name);
            }
        }

        return names;
    }

    /// <summary>A program's file by its name or its path, the user's first, null for none.</summary>
    public static string? TuiFile(string name)
    {
        if (name.EndsWith(".hor", StringComparison.OrdinalIgnoreCase) && File.Exists(name))
            return Path.GetFullPath(name);
        foreach (string folder in new[] { UserTuiFolder, EngineTuiFolder })
        {
            string file = Path.Combine(folder, name + ".hor");
            if (File.Exists(file))
                return file;
        }

        return null;
    }

    // the terminal a program is started from, for its line typed back into, between asking and the panel starting it
    private TerminalView? tuiFrom;

    /// <summary>A program started by name in a new tab ("tab"), a split on the right ("split") or under ("down"), false when there is none by that name.</summary>
    public bool RunTui(string name, string where = "tab")
    {
        if (TuiFile(name) is not { } file)
        {
            Say($"there is no program called {name}", true);
            return false;
        }

        var profile = new TerminalProfile(Path.GetFileNameWithoutExtension(file), string.Empty, "window", "tui:" + file) { Kind = TerminalProfileKind.Embedded, Tag = file };
        tuiFrom = Panel.FocusedView;
        string? folder = tuiFrom?.WorkingDirectory ?? Panel.WorkingDirectory;
        switch (where)
        {
            case "split":
                Panel.Split(SplitOrientation.Horizontal, profile, folder);
                break;
            case "down":
                Panel.Split(SplitOrientation.Vertical, profile, folder);
                break;
            default:
                Panel.NewTab(profile, folder);
                break;
        }

        return true;
    }

    /// <summary>A program of a profile that names one (tui:file), null for a profile that doesn't.</summary>
    private TerminalTui? TuiFor(TerminalProfile profile)
    {
        if (!profile.Id.StartsWith("tui:", StringComparison.Ordinal))
            return null;

        TerminalView? from = tuiFrom;
        tuiFrom = null;
        return new TerminalTui(profile.Id[4..], ServicesFor(from));
    }

    /// <summary>What a program started from a terminal may ask of the host.</summary>
    public TerminalTuiServices ServicesFor(TerminalView? from)
    {
        var repl = new HIDLRuntime();
        return new TerminalTuiServices
        {
            Folder = from?.WorkingDirectory ?? Panel.WorkingDirectory,
            Open = (path, line) => Open(path, line),
            Say = text => Say(text),
            Type = text =>
            {
                if (from is null || TabOf(from) is null)
                    return;
                Panel.FocusPane(from);
                from.Type(text);
            },
            Settings = SettingsForm,
            SetSetting = (key, value) =>
            {
                if (!Settings.Set(key, TerminalSettingsForm.ValueOf(key, value)))
                    return false;
                ApplySettings();
                SaveSettings();
                return true;
            },
            Evaluate = code => Evaluate(repl, code),
            Complete = word => NamesFor(repl, word)
        };
    }

    private TerminalTab? TabOf(TerminalView view) => Panel.TabOf(view);

    private IRuntimeValue SettingsForm()
    {
        var values = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (TerminalSettingInfo setting in TerminalSettings.Described)
        {
            if (Settings.Get(setting.Key) is { } value)
                values[setting.Key] = value;
        }

        return TerminalSettingsForm.Script(plugins.Where(plugin => !plugin.BuiltIn).Select(plugin => plugin.Id), Panel.Profiles.Select(profile => profile.Id), values);
    }

    /// <summary>A line of HIDL run, what it printed and what it came to, or what went wrong.</summary>
    public static (bool Success, string Result) Evaluate(HIDLRuntime runtime, string code)
    {
        var printed = new StringBuilder();
        runtime.Output = text => printed.Append(text).Append('\n');
        using var cancel = new CancellationTokenSource(2000);
        runtime.Interpreter.Cancellation = cancel.Token;
        try
        {
            var (success, result) = runtime.Evaluate(code);
            if (success && result.Length > 0)
                printed.Append(result);
            return (success, success ? printed.ToString().TrimEnd('\n') : result);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            runtime.Interpreter.Cancellation = CancellationToken.None;
        }
    }

    /// <summary>The names a runtime knows that start with a word, whole (math.floor for math.fl), the shortest first.</summary>
    public static IEnumerable<string> NamesFor(HIDLRuntime runtime, string word)
    {
        int dot = word.LastIndexOf('.');
        IEnumerable<string> names;
        string prefix = string.Empty;
        if (dot >= 0)
        {
            prefix = word[..(dot + 1)];
            try
            {
                names = runtime.Run(word[..dot]) is ObjectValue of ? of.Properties.Keys : [];
            }
            catch (Exception)
            {
                names = [];
            }

            word = word[(dot + 1)..];
        }
        else
        {
            names = runtime.UserScope.Variables.Keys.Concat(runtime.GlobalScope.Variables.Keys);
        }

        return names.Where(name => name.StartsWith(word, StringComparison.Ordinal)).Distinct().OrderBy(name => name.Length).ThenBy(name => name, StringComparer.Ordinal).Select(name => prefix + name).ToList();
    }
}
