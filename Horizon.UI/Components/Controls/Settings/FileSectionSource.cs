using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// A section of the settings file edited as it is written, for a program that doesn't own it (Hex's Dusk showing the
/// editor's settings). A change is written into the file straight away, the other sections kept, and the mode that owns the
/// section takes it in when it starts or sees the file change.
/// </summary>
public sealed class FileSectionSource(string? path, string section, IReadOnlyList<UISetting> settings, IReadOnlyDictionary<string, IRuntimeValue> usual) : ISettingsSource
{
    /// <summary>What one of a setting's buttons does in the mode showing it (Dusk choosing the picture), null for nothing.</summary>
    public Action<string, int>? Pressed { get; set; }

    public void Press(string key, int which) => Pressed?.Invoke(key, which);

    /// <summary>Called after a change was written, for the mode showing it to put what's its own in force (Dusk's effects and picture).</summary>
    public Action? Changed { get; set; }

    public string Section => section;

    public IReadOnlyList<UISetting> Settings => settings;

    private Dictionary<string, IRuntimeValue> Values() =>
        SettingsSections.Read(path) is { } all ? SettingsSections.Section(all, section) : [];

    public IRuntimeValue? Get(string key) => Values().TryGetValue(key, out IRuntimeValue? value) ? value : usual.GetValueOrDefault(key);

    public bool IsModified(string key) => Values().TryGetValue(key, out IRuntimeValue? value) && !SettingValues.Same(value, usual.GetValueOrDefault(key));

    public void Set(string key, IRuntimeValue value) => Write(values => values[key] = value);

    public void Reset(string key) => Write(values => values.Remove(key));

    private void Write(Action<Dictionary<string, IRuntimeValue>> change)
    {
        if (path is null)
            return;

        var values = new Dictionary<string, IRuntimeValue>(Values(), StringComparer.Ordinal);
        change(values);
        var written = new HIDLObject();
        foreach (var (key, value) in values)
            written.Add(key, value, settings.FirstOrDefault(setting => setting.Key == key)?.Help);
        try
        {
            File.WriteAllText(path, SettingsSections.Write(path, (section, written)));
        }
        catch (IOException)
        {
        }

        Changed?.Invoke();
    }
}
