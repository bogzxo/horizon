using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>What a field of a <see cref="FormPanel"/> is set with.</summary>
public enum UIFormKind
{
    Number,  // a number box
    Whole,   // a number box that only takes whole numbers
    Flag,    // a toggle
    Text,    // a text box, told about once typing has stopped
    Choice,  // a dropdown of its choices
    Flags,   // a toggle a choice, the value "standing | crouching"
    Vec      // a number box a component, two of them unless it says how many
}

/// <summary>
/// One field of a form. <paramref name="Default"/> is what it is when it isn't set (null for nothing, or for something
/// whoever reads it works out, which <paramref name="Unset"/> says in words), <paramref name="Help"/> the tooltip of
/// its name, <paramref name="Group"/> the section it goes in (none for none).
/// </summary>
public sealed record UIFormField(
    string Key,
    string Title,
    UIFormKind Kind,
    string[]? Choices = null,
    IRuntimeValue? Default = null,
    string Unset = "",
    string Help = "",
    string Group = "",
    bool ReadOnly = false,
    float Step = 1.0f,
    int Components = 2)
{
    /// <summary>A field out of what a script says about it, <c>{ key, title, kind, choices, default, unset, help, group, read_only, step, components }</c>.</summary>
    public static UIFormField FromScript(ObjectValue said)
    {
        var properties = said.Properties;
        string Text(string key, string otherwise = "") => properties.TryGetValue(key, out var value) && value is StringValue text ? text.Value : otherwise;
        float Number(string key, float otherwise) => properties.TryGetValue(key, out var value) && value is NumberValue number ? number.Value : otherwise;

        string key = Text("key");
        if (key.Length == 0)
            throw new HidlRuntimeException("a field of a form needs a key.");

        string kind = Text("kind", "text");
        if (!Enum.TryParse(kind, ignoreCase: true, out UIFormKind parsed))
            throw new HidlRuntimeException($"'{kind}' isn't a kind of field, number, whole, flag, text, choice, flags or vec.");

        string[]? choices = properties.TryGetValue("choices", out var listed) && listed is ListValue list
            ? [.. list.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)]
            : null;

        return new UIFormField(key, Text("title", key.Replace('_', ' ')), parsed, choices,
            properties.TryGetValue("default", out var fallback) && fallback is not NullValue ? fallback : null,
            Text("unset"), Text("help"), Text("group"),
            properties.TryGetValue("read_only", out var locked) && locked is BooleanValue { Value: true },
            Number("step", parsed == UIFormKind.Whole ? 1.0f : 0.1f), (int)Number("components", 2));
    }
}
