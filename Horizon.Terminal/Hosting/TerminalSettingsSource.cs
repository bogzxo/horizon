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

    // the terminal's settings as rows, in the groups the forms have, the lists ticked from what there is
    private static List<UISetting> Describe(TerminalHost host, string? themeGroup = null)
    {
        var rows = new List<UISetting>();
        foreach (TerminalSettingInfo setting in TerminalSettings.Described)
        {
            UIFormKind kind = setting.Kind switch
            {
                "choice" => UIFormKind.Choice,
                "flag" => UIFormKind.Flag,
                "number" => UIFormKind.Number,
                "whole" => UIFormKind.Whole,
                "list" => UIFormKind.Flags,
                _ => UIFormKind.Text
            };
            string[]? choices = setting.Key switch
            {
                "disabled_plugins" => [.. host.Plugins.Where(plugin => !plugin.BuiltIn).Select(plugin => plugin.Id)],
                "plain_profiles" => [.. host.Panel.Profiles.Select(profile => profile.Id)],
                _ => setting.Choices
            };
            // The theme goes where the program says (Hex has Dusk's with Dawn's, under how Hex looks), the rest are the terminal's own
            bool theme = setting.Key == "theme";
            rows.Add(new UISetting(setting.Key, theme ? "Dusk's theme" : Title(setting.Key), kind, setting.Help,
                theme ? themeGroup ?? "Terminal · Theme" : "Terminal · " + TerminalSettingsForm.Group(setting.Key), choices,
                setting.Least, setting.Most, kind == UIFormKind.Number ? 0.01f : 1.0f));
        }

        return rows;
    }

    /// <summary>text_scale as "Text scale".</summary>
    public static string Title(string key) => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');
}
