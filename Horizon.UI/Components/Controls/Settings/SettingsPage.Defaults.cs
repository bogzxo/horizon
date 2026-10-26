using System.Globalization;

using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

// What a row's reset puts back, said on it, and the rows that hang off a switch fading while it's off
public partial class SettingsPage
{
    /// <summary>What a reset says when nobody knows what it puts back, in English, the texts' own (<see cref="SettingsTexts.ResetToDefault"/>) unless a program gives others.</summary>
    public const string RESET_TO_DEFAULT = "Reset to default";

    /// <summary>What a setting comes as, what its reset puts back, null where nobody says.</summary>
    public Func<string, IRuntimeValue?>? DefaultOf { get; set; }

    /// <summary>
    /// What a setting's reset says it puts back, "Reset to 4", "Reset to On" or a choice's label, the way a slider of it
    /// writes a number ("Reset to 22%"). <see cref="SettingsTexts.ResetToDefault"/> when nobody says what it comes as or it comes as
    /// nothing written (an empty path).
    /// </summary>
    public string ResetText(string key)
    {
        UISetting? setting = settings.Find(each => each.Key == key);
        if (setting is null || DefaultOf?.Invoke(key) is not { } usual)
            return texts.ResetToDefault;

        string? said = usual switch
        {
            BooleanValue flag => flag.Value ? texts.On : texts.Off,
            NumberValue number when setting.Slider => SettingRow.Said(setting, number.Value),
            NumberValue number => setting.Unit.Length > 0 ? $"{Written(number.Value)} {setting.Unit}" : Written(number.Value),
            StringValue { Value.Length: > 0 } text when setting.Choices is { Length: > 0 } => setting.LabelOf(text.Value),
            StringValue { Value.Length: > 0 } text => text.Value,
            _ => null
        };
        return said is null ? texts.ResetToDefault : SettingsTexts.Fill(texts.ResetTo, ("value", said));
    }

    private static string Written(float number) => number.ToString("0.##", CultureInfo.InvariantCulture);

    // Whether the switch a setting hangs off is off, false for one that hangs off nothing or a switch the page hasn't got
    private bool ParentOff(UISetting setting) =>
        setting.DependsOn.Length > 0 && ValueOf?.Invoke(setting.DependsOn) is BooleanValue { Value: false };

    // The rows that hang off a setting faded or brought back by what it is now
    private void RefreshDependents(string key)
    {
        foreach (SettingRow row in rowOf.Values)
        {
            if (row.Setting.DependsOn == key)
                row.Dimmed = ParentOff(row.Setting);
        }
    }
}
