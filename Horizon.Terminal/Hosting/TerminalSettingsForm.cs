using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// The terminal's settings as a <see cref="FormPanel"/> takes them, a field a setting out of
/// <see cref="TerminalSettings.Described"/>, in sections, the lists as flags of what there is to pick (the plugins, the
/// profiles). Dusk's settings window and Hex's page for settings.hor are the same form.
/// </summary>
public static class TerminalSettingsForm
{
    /// <summary>The fields, the plugins and profiles there are for the lists' choices.</summary>
    public static List<UIFormField> Fields(IEnumerable<string> plugins, IEnumerable<string> profiles)
    {
        string[] pluginIds = [.. plugins], profileIds = [.. profiles];
        var fields = new List<UIFormField>();
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
                "disabled_plugins" => pluginIds,
                "plain_profiles" => profileIds,
                _ => setting.Choices
            };
            fields.Add(new UIFormField(setting.Key, setting.Key.Replace('_', ' '), kind, choices, null, $"{setting.Default} as it comes",
                setting.Help, Group(setting.Key), Step: kind == UIFormKind.Number ? 0.01f : 1.0f));
        }

        return fields;
    }

    /// <summary>Which section of the form a setting is in.</summary>
    public static string Group(string key) => key switch
    {
        "theme" or "scale" or "font" or "text_scale" => "How it looks",
        "animations" or "animation_time" or "cursor_glide" => "What moves",
        "profile" or "wsl" or "close_on_exit" or "start_in" or "shell_integration" => "Shells",
        "highlight_line" or "plain_profiles" or "completion" or "completion_tools" or "highlight_output" => "Colours",
        "copy_on_select" or "right_click_paste" or "bell" or "clipboard_write" or "quake_key" => "Mouse, bell and window",
        "history" or "history_size" or "scrollback" or "restore_sessions" => "What is kept",
        _ => "Plugins"
    };

    /// <summary>The settings as the form's values, a list as the flags it is ("a | b").</summary>
    public static Dictionary<string, IRuntimeValue> Values(TerminalSettings settings) => Values(key => settings.Get(key));

    /// <summary>What a settings file says, as the form's values, only the ones it says.</summary>
    public static Dictionary<string, IRuntimeValue> Values(Dictionary<string, IRuntimeValue> said) => Values(key => said.GetValueOrDefault(key));

    private static Dictionary<string, IRuntimeValue> Values(Func<string, IRuntimeValue?> read)
    {
        var values = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (TerminalSettingInfo setting in TerminalSettings.Described)
        {
            if (read(setting.Key) is not { } value)
                continue;
            values[setting.Key] = setting.Kind == "list" && value is ListValue list
                ? new StringValue(string.Join(" | ", list.Items.OfType<StringValue>().Select(item => item.Value)))
                : value;
        }

        return values;
    }

    /// <summary>What the form says of a setting, as the settings take it, a list's flags back into a list.</summary>
    public static IRuntimeValue ValueOf(string key, IRuntimeValue value)
    {
        if (Array.Find(TerminalSettings.Described, setting => setting.Key == key) is { Kind: "list" } && value is StringValue flags)
            return Natives.List(flags.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return value;
    }

    /// <summary>What a setting comes as, for a field that is cleared.</summary>
    public static IRuntimeValue? DefaultOf(string key) => new TerminalSettings(null).Get(key);

    /// <summary>The form as a script takes it, <c>{ fields: [...], values: {...} }</c>, Hex's page for settings.hor.</summary>
    public static ObjectValue Script(IEnumerable<string> plugins, IEnumerable<string> profiles, Dictionary<string, IRuntimeValue> values)
    {
        var fields = new List<IRuntimeValue>();
        foreach (UIFormField field in Fields(plugins, profiles))
        {
            fields.Add(Natives.Object(
                ("key", new StringValue(field.Key)),
                ("title", new StringValue(field.Title)),
                ("kind", new StringValue(field.Kind.ToString().ToLowerInvariant())),
                ("choices", Natives.List(field.Choices ?? [])),
                ("help", new StringValue(field.Help)),
                ("unset", new StringValue(field.Unset)),
                ("group", new StringValue(field.Group))));
        }

        return Natives.Object(("fields", new ListValue(fields)), ("values", new ObjectValue(Values(values))));
    }
}
