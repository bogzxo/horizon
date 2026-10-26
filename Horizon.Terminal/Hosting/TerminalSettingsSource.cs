using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// The terminal's section, Dusk's settings and Dawn's terminal page's, put in force through the host that has them
/// (applied to every terminal, written to the file) the moment one changes.
/// </summary>
public sealed class TerminalSettingsSource(TerminalHost host, string? themeGroup = null) : ISettingsSource
{
    private static readonly TerminalSettings Usual = new(null);

    public string Section => SettingsSections.TERMINAL;

    public IReadOnlyList<UISetting> Settings { get; } = Describe(host, themeGroup);

    /// <summary>Called after a setting was put in force, for a program that does more with one (Dusk's theme and scale).</summary>
    public Action? Changed { get; set; }

    public IRuntimeValue? Get(string key) => host.Settings.Get(key);

    public bool IsModified(string key) => !SettingValues.Same(host.Settings.Get(key), Usual.Get(key));

    public void Set(string key, IRuntimeValue value)
    {
        if (!host.Settings.Set(key, TerminalSettingsForm.ValueOf(key, value)))
            return;
        host.ApplySettings();
        host.SaveSettings();
        Changed?.Invoke();
    }

    public void Reset(string key)
    {
        if (Usual.Get(key) is { } comesAs)
            Set(key, comesAs);
    }

    // How far apart the ends of a number can be for it to be a slider, and how many steps one goes in
    private const float SLIDES_UP_TO = 3.0f, SLIDER_STEPS = 100.0f;

    // the terminal's settings as rows, in the groups the forms have, the lists ticked from what there is
    private static List<UISetting> Describe(TerminalHost host, string? themeGroup = null)
    {
        var rows = new List<UISetting>();
        foreach (TerminalSettingInfo setting in TerminalSettings.Described)
        {
            // The font and the shell are picked from what there is, a name typed wrong was no font at all or a tab of nothing
            UIFormKind kind = setting.Key is "font" or "profile" ? UIFormKind.Choice : setting.Kind switch
            {
                "choice" => UIFormKind.Choice,
                "flag" => UIFormKind.Flag,
                "number" => UIFormKind.Number,
                "whole" => UIFormKind.Whole,
                "list" => UIFormKind.Flags,
                _ => UIFormKind.Text
            };
            string[]? labels = null;
            string[]? choices = setting.Key switch
            {
                "disabled_plugins" => [.. host.Plugins.Where(plugin => !plugin.BuiltIn).Select(plugin => plugin.Id)],
                "plain_profiles" => [.. host.Panel.Profiles.Select(profile => profile.Id)],
                "font" => FontChoices(host.Settings.Font, out labels),
                "profile" => ProfileChoices(host, out labels),
                _ => setting.Choices
            };
            // The theme goes where the program says (Hex has Dusk's with Dawn's, under how Hex looks), the rest are the terminal's own
            bool theme = setting.Key == "theme";

            // A number between two that aren't far apart is a slider, the way Dawn's caret glide is. Cursor glide was a box
            // to type into under a slider that did the same for the caret
            bool slides = kind == UIFormKind.Number && setting.Most > setting.Least && setting.Most - setting.Least <= SLIDES_UP_TO;
            float step = slides ? (setting.Most - setting.Least) / SLIDER_STEPS : kind == UIFormKind.Number ? 0.01f : 1.0f;
            string unit = setting.Key.EndsWith("_glide", StringComparison.Ordinal) || setting.Key.EndsWith("_time", StringComparison.Ordinal) ? "s"
                : setting.Key == "scale" ? "×" : string.Empty;
            rows.Add(new UISetting(setting.Key, theme ? "Dusk's theme" : Title(setting.Key), kind, setting.Help,
                theme ? themeGroup ?? "Terminal · Theme" : "Terminal · " + TerminalSettingsForm.Group(setting.Key), choices,
                setting.Least, setting.Most, step, Slider: slides, Unit: unit, ChoiceLabels: labels));
        }

        return rows;
    }

    /// <summary>
    /// The fonts the terminals can write in, empty for the skin's and then every folder of Assets/fonts with a .ttf of its
    /// own name in it (the code's font row goes by the same), Hack first. The one set now is kept on the end when it isn't
    /// among them, a font of somebody's own still shows as what it is.
    /// </summary>
    public static string[] FontChoices(string current, out string[] labels)
    {
        string fonts = Path.Combine(Horizon.Core.Platform.PlatformPaths.Program, "Assets", "fonts");
        var found = new List<string> { string.Empty };
        if (Directory.Exists(fonts))
        {
            found.AddRange(Directory.GetDirectories(fonts).Select(Path.GetFileName).OfType<string>()
                .Where(name => File.Exists(Path.Combine(fonts, name, name + ".ttf")))
                .OrderBy(name => name == "hack" ? 0 : 1).ThenBy(name => name, StringComparer.OrdinalIgnoreCase));
        }

        if (!found.Contains(current, StringComparer.OrdinalIgnoreCase))
            found.Add(current);

        labels = [.. found.Select(font => font.Length == 0 ? "The skin's font" : string.Empty)];
        return [.. found];
    }

    /// <summary>
    /// The shells a new tab can open with by their ids, empty for the first one found and then the profiles there are,
    /// each called by its name in the dropdown. The one set now is kept on the end when it isn't among them (a WSL
    /// distro that's gone), so the row still says what the file does.
    /// </summary>
    public static string[] ProfileChoices(TerminalHost host, out string[] labels)
    {
        var ids = new List<string> { string.Empty };
        var names = new List<string> { "The first one found" };
        foreach (TerminalProfile profile in host.Panel.Profiles)
        {
            if (profile.Id.Length == 0 || ids.Contains(profile.Id, StringComparer.Ordinal))
                continue;
            ids.Add(profile.Id);
            names.Add(profile.Name.Length > 0 ? profile.Name : profile.Id);
        }

        string current = host.Settings.Profile;
        if (!ids.Contains(current, StringComparer.Ordinal))
        {
            ids.Add(current);
            names.Add(current);
        }

        labels = [.. names];
        return [.. ids];
    }

    /// <summary>text_scale as "Text scale".</summary>
    public static string Title(string key) => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');
}
