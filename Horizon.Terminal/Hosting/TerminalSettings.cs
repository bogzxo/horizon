using System.Globalization;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.Logging;

namespace Horizon.UI.Components;

/// <summary>One setting as an editor shows it, its key, its kind (flag, number, whole, text, choice or list), what it comes as and a line of help.</summary>
public sealed record TerminalSettingInfo(string Key, string Kind, string Default, string Help, string[]? Choices = null, float Least = 0.0f, float Most = 0.0f);

/// <summary>
/// What a terminal application is set to, kept in settings.hor, a HIDL file anybody can read and edit
/// (<c>let settings = { theme: "horizon", ... }</c>), shared by Dusk and Hex's terminal page. Read as the program
/// starts and again when the file changes, written when something is changed from inside. A file that is missing or
/// makes no sense is everything at what it comes as, a line it leaves out stays at that too, and a key it has that
/// isn't one of these is kept as it was and written back, for a newer program or a plugin of its own.
/// </summary>
public sealed class TerminalSettings
{
    public const string FILE = "settings.hor";

    /// <summary>Which theme of the flat skin the window and the terminals are in, Dusk's, Hex has its own.</summary>
    public string Theme = "horizon";

    /// <summary>The id of the profile a new tab opens with, empty for the first one found.</summary>
    public string Profile = string.Empty;

    /// <summary>The scale of the terminals' text, Hack at 0.16 reads well at a window of 1600 by 900.</summary>
    public float TextScale = 0.16f;

    /// <summary>How big the window's own parts (the tabs, the bars) are drawn, Dusk's.</summary>
    public float Scale = 1.0f;

    /// <summary>How many rows every terminal keeps of what went off the top.</summary>
    public int Scrollback = 5000;

    /// <summary>Whether anything moves, splits sliding open, tabs sliding over, the cursor's glide, and how long each takes.</summary>
    public bool Animations = true;
    public float AnimationTime = 0.16f;

    /// <summary>How long the cursor takes to glide most of the way to its next cell, 0 for it jumping there.</summary>
    public float CursorGlide = TerminalView.DEFAULT_CURSOR_GLIDE;

    /// <summary>The font the terminals write in, a folder of Assets/fonts, empty for the skin's.</summary>
    public string Font = "hack";

    /// <summary>Whether the WSL distributions are asked for as profiles (wsl -l takes a moment on a machine without it).</summary>
    public bool Wsl = true;

    /// <summary>Whether a pane closes by itself when its shell ends.</summary>
    public bool CloseOnExit = true;

    /// <summary>Whether Up and Down step through the program's own history of the lines run (history.txt), and how many lines it keeps.</summary>
    public bool History = true;
    public int HistorySize = TerminalHistory.DEFAULT_LIMIT;

    /// <summary>Whether shells are started with the script that has them say their marks and their folder (TerminalIntegration).</summary>
    public bool ShellIntegration = true;

    /// <summary>Whether a program may set the clipboard (OSC 52), off unless somebody says, a program over SSH could put anything on it.</summary>
    public bool ClipboardWrite;

    /// <summary>Whether the tabs that were open come back on the next start, their profiles, splits and folders.</summary>
    public bool RestoreSessions = true;

    /// <summary>Where a new terminal starts in Hex, the workspace's folder or the folder of the file on screen.</summary>
    public string StartIn = "workspace";

    /// <summary>Whether what is typed at a prompt is coloured as it is typed, commands green when they are there and red when not, paths underlined.</summary>
    public bool HighlightLine = true;

    /// <summary>The profiles whose typed line is left the shell's own colours, by id.</summary>
    public readonly HashSet<string> PlainProfiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether cmd and bash get a list of the files and commands that fit what is being typed, taken with tab.</summary>
    public bool Completion = true;

    /// <summary>The tools whose words are completed out of their own --help (and git's branches), the only ones that are ever run for it.</summary>
    public readonly HashSet<string> CompletionTools = new(TerminalToolCompletion.DefaultTools, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether what programs write is coloured by what it looks like (paths, errors, diffs) where they left it plain.</summary>
    public bool HighlightOutput;

    /// <summary>Whether a selection is copied the moment it is made, and a right click pastes.</summary>
    public bool CopyOnSelect;
    public bool RightClickPaste;

    /// <summary>What the bell does, "flash", "sound" or "none".</summary>
    public string Bell = "flash";

    /// <summary>The key that drops Dusk's window from the top of the screen and takes it back up, empty for none.</summary>
    public string QuakeKey = string.Empty;

    /// <summary>The plugins that are switched off, by the name of their folder.</summary>
    public readonly HashSet<string> DisabledPlugins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the file had that isn't one of these, kept and written back as it was.</summary>
    public readonly Dictionary<string, IRuntimeValue> Extra = new(StringComparer.Ordinal);

    /// <summary>Every setting, for an editor's completion, hover and form, and api.hor.</summary>
    public static readonly TerminalSettingInfo[] Described =
    [
        new("theme", "choice", "\"horizon\"", "The colours Dusk is drawn in, its terminals' sixteen colours made from them.",
            ["horizon", "dark", "graphite", "light", "midnight", "slate", "sunset"]),
        new("profile", "text", "\"\"", "The id of the shell a new tab opens with (pwsh, powershell, cmd, git_bash, wsl:Ubuntu), empty for the first one found."),
        new("text_scale", "number", "0.16", "How big the terminals' text is, Ctrl+Shift+= and - change it.", Least: 0.06f, Most: 0.6f),
        new("scale", "number", "1", "How big Dusk's own parts, the tabs and the bars, are drawn.", Least: 0.5f, Most: 3.0f),
        new("scrollback", "whole", "5000", "How many rows every terminal keeps of what went off the top.", Least: 0, Most: 100000),
        new("animations", "flag", "true", "Whether anything moves, splits sliding open, tabs sliding over, a tab folding away, the cursor's glide."),
        new("animation_time", "number", "0.16", "How long each of those takes, in seconds.", Least: 0.02f, Most: 1.0f),
        new("cursor_glide", "number", "0.036", "How long the cursor takes to glide to where it goes, in seconds, 0 for it jumping there. It jumps anyway with the animations off.", Least: 0.0f, Most: 0.3f),
        new("font", "text", "\"hack\"", "The font the terminals write in, a folder of Assets/fonts, empty for the skin's."),
        new("wsl", "flag", "true", "Whether the WSL distributions are asked for as profiles."),
        new("close_on_exit", "flag", "true", "Whether a pane closes by itself when its shell ends."),
        new("history", "flag", "true", "Whether Up and Down step the history of the lines run in every shell, kept in history.txt. Off, they are the shell's own."),
        new("history_size", "whole", "1000", "How many lines the history keeps.", Least: 10, Most: 100000),
        new("restore_sessions", "flag", "true", "Whether the tabs that were open come back on the next start."),
        new("shell_integration", "flag", "true", "Whether PowerShell and bash are started with a script that has them say where their prompts and commands are, for the blocks, the jumps between commands and the colours of the line. Nothing is written into a profile."),
        new("clipboard_write", "flag", "false", "Whether a program may set the clipboard (OSC 52, a yank in vim over SSH). Off, a program could put anything on it."),
        new("start_in", "choice", "\"workspace\"", "Where a new terminal starts in Hex, the workspace's folder or the folder of the file on screen.", ["workspace", "file"]),
        new("highlight_line", "flag", "true", "Whether what is typed at a prompt is coloured as it is typed, a command green when it is there and red when it isn't, flags, strings and variables, and paths that exist underlined. PSReadLine's own colours are kept."),
        new("plain_profiles", "list", "[]", "The profiles whose typed line is left alone, by id (pwsh, cmd, wsl:Ubuntu)."),
        new("completion", "flag", "true", "Whether cmd and bash get a list of the files and commands that fit what is being typed, tab takes one, escape puts it away. PowerShell does files and commands itself, it gets the tools' words below. fish is left to itself."),
        new("completion_tools", "list", "[" + string.Join(", ", TerminalToolCompletion.DefaultTools.Select(tool => $"\"{tool}\"")) + "]",
            "The tools whose subcommands and flags are completed out of what their --help says, and git's branches, remotes and changed files. Only these are ever run for it, a script that doesn't know --help would just run."),
        new("highlight_output", "flag", "false", "Whether what programs write is coloured by what it looks like where they left it plain, paths, errors and warnings, diffs, stack frames, numbers."),
        new("copy_on_select", "flag", "false", "Whether a selection is copied the moment it is made."),
        new("right_click_paste", "flag", "false", "Whether a right click pastes instead of opening the menu."),
        new("bell", "choice", "\"flash\"", "What the bell does.", ["flash", "sound", "none"]),
        new("quake_key", "text", "\"\"", "A key anywhere in Windows that drops Dusk's window from the top of the screen and takes it back up (Ctrl+`, F12), empty for none."),
        new("disabled_plugins", "list", "[]", "The plugins of the user's that are switched off, by the name of their folder. The ones the terminal comes with are always on.")
    ];

    /// <summary>Where it is kept, null for settings that are never read or written (a self test).</summary>
    public string? Path { get; }

    /// <param name="path">Where it is kept, null for settings that are never read or written (a self test).</param>
    public TerminalSettings(string? path)
    {
        Path = path;
        Reload();
    }

    /// <summary>Reads the file again, what it doesn't say left as it is. False with the reason logged when it doesn't read.</summary>
    public bool Reload()
    {
        if (Path is null || !File.Exists(Path))
            return true;

        try
        {
            Read(ReadObject(File.ReadAllText(Path)));
            return true;
        }
        catch (Exception e)
        {
            Log.Warning($"[Terminal] {Path} makes no sense and is left alone: {e.Message}");
            return false;
        }
    }

    /// <summary>The settings object a settings file declares. Throws when it doesn't.</summary>
    public static Dictionary<string, IRuntimeValue> ReadObject(string text) =>
        SettingsSections.Section(SettingsSections.ReadText(text), SettingsSections.TERMINAL);

    /// <summary>Takes the settings out of an object, what it doesn't say staying as it was.</summary>
    public void Read(Dictionary<string, IRuntimeValue> values)
    {
        Theme = Text(values, "theme", Theme);
        Profile = Text(values, "profile", Profile);
        TextScale = Math.Clamp(Number(values, "text_scale", TextScale), 0.06f, 0.6f);
        Scale = Math.Clamp(Number(values, "scale", Scale), 0.5f, 3.0f);
        Scrollback = (int)Math.Clamp(Number(values, "scrollback", Scrollback), 0, 100000);
        Animations = Flag(values, "animations", Animations);
        AnimationTime = Math.Clamp(Number(values, "animation_time", AnimationTime), 0.02f, 1.0f);
        CursorGlide = Math.Clamp(Number(values, "cursor_glide", CursorGlide), 0.0f, 0.3f);
        Font = Text(values, "font", Font);
        Wsl = Flag(values, "wsl", Wsl);
        CloseOnExit = Flag(values, "close_on_exit", CloseOnExit);
        History = Flag(values, "history", History);
        HistorySize = (int)Math.Clamp(Number(values, "history_size", HistorySize), 10, 100000);
        RestoreSessions = Flag(values, "restore_sessions", RestoreSessions);
        ClipboardWrite = Flag(values, "clipboard_write", ClipboardWrite);
        ShellIntegration = Flag(values, "shell_integration", ShellIntegration);
        StartIn = Text(values, "start_in", StartIn);
        HighlightLine = Flag(values, "highlight_line", HighlightLine);
        Completion = Flag(values, "completion", Completion);
        HighlightOutput = Flag(values, "highlight_output", HighlightOutput);
        CopyOnSelect = Flag(values, "copy_on_select", CopyOnSelect);
        RightClickPaste = Flag(values, "right_click_paste", RightClickPaste);
        Bell = Text(values, "bell", Bell) is "flash" or "sound" or "none" ? Text(values, "bell", Bell) : Bell;
        QuakeKey = Text(values, "quake_key", QuakeKey);
        if (values.TryGetValue("plain_profiles", out var plain) && plain is ListValue plainList)
        {
            PlainProfiles.Clear();
            foreach (IRuntimeValue name in plainList.Items)
                if (name is StringValue { Value: { } named }) PlainProfiles.Add(named);
        }

        if (values.TryGetValue("completion_tools", out var tools) && tools is ListValue toolList)
        {
            CompletionTools.Clear();
            foreach (IRuntimeValue name in toolList.Items)
                if (name is StringValue { Value.Length: > 0 } named) CompletionTools.Add(named.Value);
        }

        if (values.TryGetValue("disabled_plugins", out var disabled) && disabled is ListValue off)
        {
            DisabledPlugins.Clear();
            foreach (IRuntimeValue name in off.Items)
                if (name is StringValue { Value: { } named }) DisabledPlugins.Add(named);
        }

        foreach (var (key, value) in values)
        {
            if (!IsKey(key))
                Extra[key] = value;
        }
    }

    /// <summary>Whether a key is one of the settings.</summary>
    public static bool IsKey(string key) => Array.Exists(Described, setting => setting.Key == key);

    /// <summary>One setting by its key, as a plugin asks for it, null for a key that isn't one.</summary>
    public IRuntimeValue? Get(string key) => key switch
    {
        "theme" => new StringValue(Theme),
        "profile" => new StringValue(Profile),
        "text_scale" => new NumberValue(TextScale),
        "scale" => new NumberValue(Scale),
        "scrollback" => new NumberValue(Scrollback),
        "animations" => Values.Bool(Animations),
        "animation_time" => new NumberValue(AnimationTime),
        "cursor_glide" => new NumberValue(CursorGlide),
        "font" => new StringValue(Font),
        "wsl" => Values.Bool(Wsl),
        "close_on_exit" => Values.Bool(CloseOnExit),
        "history" => Values.Bool(History),
        "history_size" => new NumberValue(HistorySize),
        "restore_sessions" => Values.Bool(RestoreSessions),
        "clipboard_write" => Values.Bool(ClipboardWrite),
        "shell_integration" => Values.Bool(ShellIntegration),
        "start_in" => new StringValue(StartIn),
        "highlight_line" => Values.Bool(HighlightLine),
        "plain_profiles" => Natives.List(PlainProfiles.Order(StringComparer.OrdinalIgnoreCase)),
        "completion" => Values.Bool(Completion),
        "completion_tools" => Natives.List(CompletionTools.Order(StringComparer.OrdinalIgnoreCase)),
        "highlight_output" => Values.Bool(HighlightOutput),
        "copy_on_select" => Values.Bool(CopyOnSelect),
        "right_click_paste" => Values.Bool(RightClickPaste),
        "bell" => new StringValue(Bell),
        "quake_key" => new StringValue(QuakeKey),
        "disabled_plugins" => Natives.List(DisabledPlugins.Order(StringComparer.OrdinalIgnoreCase)),
        _ => Extra.TryGetValue(key, out IRuntimeValue? kept) ? kept : null
    };

    /// <summary>One setting set by its key, as a plugin does it. False for a key that isn't one.</summary>
    public bool Set(string key, IRuntimeValue value)
    {
        if (!IsKey(key))
            return false;

        Read(new Dictionary<string, IRuntimeValue> { [key] = value });
        return true;
    }

    private static float Number(Dictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out var value) && value is NumberValue number ? number.Value : otherwise;

    private static bool Flag(Dictionary<string, IRuntimeValue> values, string key, bool otherwise) =>
        values.TryGetValue(key, out var value) && value is BooleanValue flag ? flag.Value : otherwise;

    private static string Text(Dictionary<string, IRuntimeValue> values, string key, string otherwise) =>
        values.TryGetValue(key, out var value) && value is StringValue text ? text.Value : otherwise;

    /// <summary>The settings as their file, every one of them with its line of help over it.</summary>
    public string Write()
    {
        var settings = new HIDLObject();
        foreach (TerminalSettingInfo setting in Described)
            settings.Add(setting.Key, Get(setting.Key)!, setting.Help);
        foreach (var (key, value) in Extra.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            settings.Add(key, value);

        // the terminal's section of Hex's one settings file, the editor's and the extensions' kept as the file has them
        return SettingsSections.Write(Path, (SettingsSections.TERMINAL, settings));
    }

    /// <summary>Writes the settings to their file. Says what went wrong if they couldn't be, null if they could.</summary>
    public string? Save()
    {
        if (Path is null)
            return null;

        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder)
                Directory.CreateDirectory(folder);
            File.WriteAllText(Path, Write());
            return null;
        }
        catch (Exception e)
        {
            return $"couldn't keep the settings for next time: {e.Message}";
        }
    }

    /// <summary>A number as a settings file writes it.</summary>
    public static string Written(float value) => value.ToString(CultureInfo.InvariantCulture);
}
