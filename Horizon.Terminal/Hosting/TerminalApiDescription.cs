using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// What a terminal program is, written down for an editor. The settings of settings.hor with their kinds, defaults
/// and help, the commands there are with their titles and keys, the functions of the terminal object with what they
/// take and a line each, and the hooks a plugin can have. A host writes it as api.hor in the terminal's folder
/// (<see cref="TerminalHost.WriteApi"/>), Dusk and Hex both, each adding what is its own to what is there, and Hex reads
/// it to complete, explain and check Dusk's files (<see cref="TerminalFileKnowledge"/>).
/// </summary>
public sealed class TerminalApiDescription
{
    public const string FILE = "api.hor";

    public List<TerminalSettingInfo> Settings { get; } = [];

    /// <summary>The commands by name, their title and their key, in the order they were first seen.</summary>
    public Dictionary<string, (string Title, string Key)> Commands { get; } = new(StringComparer.Ordinal);

    /// <summary>The functions of the terminal object by name, and which programs have each ("dusk", "hex", empty for every one).</summary>
    public Dictionary<string, (TerminalApiFunction Function, string Programs)> Functions { get; } = new(StringComparer.Ordinal);

    public List<(string Name, string Parameters, string About)> Hooks { get; } = [];

    /// <summary>A program's description out of its host, what every program has and what it adds.</summary>
    public static TerminalApiDescription Of(TerminalHost host, IEnumerable<TerminalApiFunction> programFunctions)
    {
        var made = Engine();
        foreach (TerminalCommand command in host.Commands.All)
            made.Commands[command.Name] = (command.Title, command.Key);
        foreach (TerminalApiFunction function in programFunctions)
            made.Functions[function.Name] = (function, host.Options.Name);
        return made;
    }

    /// <summary>What every program has, the engine's own, without any program's commands.</summary>
    public static TerminalApiDescription Engine()
    {
        var made = new TerminalApiDescription();
        made.Settings.AddRange(TerminalSettings.Described);
        foreach (TerminalApiFunction function in TerminalHost.CoreFunctions)
            made.Functions[function.Name] = (function, string.Empty);
        made.Hooks.AddRange(TerminalPlugin.Hooks);
        return made;
    }

    /// <summary>Another description laid over this one, its commands and functions added, the rest as this has it.</summary>
    public void Merge(TerminalApiDescription other)
    {
        foreach (var (name, command) in other.Commands)
            Commands[name] = command;
        foreach (var (name, (function, programs)) in other.Functions)
        {
            if (!Functions.TryGetValue(name, out var mine))
            {
                Functions[name] = (function, programs);
                continue;
            }

            // Every program's stays every program's, one that two programs have is both of theirs
            if (mine.Programs.Length == 0 || programs.Length == 0)
                Functions[name] = (function, string.Empty);
            else
                Functions[name] = (function, string.Join(", ", mine.Programs.Split(", ").Concat(programs.Split(", ")).Distinct().Order()));
        }
    }

    /// <summary>The description as its file.</summary>
    public string Write()
    {
        var settings = new HIDLObject();
        foreach (TerminalSettingInfo setting in Settings)
        {
            var about = new HIDLObject().Add("kind", setting.Kind).Add("default", setting.Default).Add("help", setting.Help);
            if (setting.Choices is { Length: > 0 } choices)
                about.Add("choices", Natives.List(choices));
            if (setting.Most > setting.Least)
                about.Add("least", setting.Least).Add("most", setting.Most);
            settings.Add(setting.Key, about);
        }

        var commands = new HIDLObject();
        foreach (var (name, (title, key)) in Commands)
            commands.Add(name, new HIDLObject().Add("title", title).Add("key", key));

        var functions = new HIDLObject();
        foreach (var (name, (function, programs)) in Functions)
            functions.Add(name, new HIDLObject().Add("parameters", function.Parameters).Add("help", function.Help).Add("programs", programs));

        var hooks = new HIDLObject();
        foreach (var (name, parameters, about) in Hooks)
            hooks.Add(name, new HIDLObject().Add("parameters", parameters).Add("help", about));

        return new HIDLDocument()
            .Comment("What the terminal is, written by Dusk and by Hex as they start, for an editor to know its files by. Settings,\n" +
                "commands with their keys, the functions of the terminal object a plugin is handed and the hooks a plugin can have.\n" +
                "Written again every start, editing it changes nothing.")
            .Define("dusk")
            .Let("api", new HIDLObject().Add("settings", settings).Add("commands", commands).Add("functions", functions).Add("hooks", hooks))
            .Write();
    }

    /// <summary>A description out of its file, null for one that doesn't read.</summary>
    public static TerminalApiDescription? Read(string text)
    {
        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, _) = runtime.Evaluate(text);
            if (!success || runtime.UserScope.Lookup("api") is not ObjectValue api)
                return null;

            var read = new TerminalApiDescription();
            if (api.Properties.GetValueOrDefault("settings") is ObjectValue settings)
            {
                foreach (var (key, value) in settings.Properties)
                {
                    if (value is not ObjectValue setting)
                        continue;
                    string[]? choices = setting.Properties.GetValueOrDefault("choices") is ListValue list ? [.. list.Items.OfType<StringValue>().Select(item => item.Value)] : null;
                    float least = setting.Properties.GetValueOrDefault("least") is NumberValue l ? l.Value : 0.0f;
                    float most = setting.Properties.GetValueOrDefault("most") is NumberValue m ? m.Value : 0.0f;
                    read.Settings.Add(new TerminalSettingInfo(key, Text(setting, "kind"), Text(setting, "default"), Text(setting, "help"), choices, least, most));
                }
            }

            if (api.Properties.GetValueOrDefault("commands") is ObjectValue commands)
            {
                foreach (var (name, value) in commands.Properties)
                    if (value is ObjectValue command) read.Commands[name] = (Text(command, "title"), Text(command, "key"));
            }

            if (api.Properties.GetValueOrDefault("functions") is ObjectValue functions)
            {
                foreach (var (name, value) in functions.Properties)
                    if (value is ObjectValue function)
                        read.Functions[name] = (new TerminalApiFunction(name, Text(function, "parameters"), Text(function, "help")), Text(function, "programs"));
            }

            if (api.Properties.GetValueOrDefault("hooks") is ObjectValue hooks)
            {
                foreach (var (name, value) in hooks.Properties)
                    if (value is ObjectValue hook) read.Hooks.Add((name, Text(hook, "parameters"), Text(hook, "help")));
            }

            return read;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Text(ObjectValue of, string key) =>
        of.Properties.TryGetValue(key, out IRuntimeValue? value) && value is StringValue text ? text.Value : string.Empty;
}
