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
    Vec,     // a number box a component, two of them unless it says how many
    Pairs,   // a list of key = value lines, an object of texts by text (*.orig as csharp)
    List     // a list of texts, a line each, globs to leave out say (**/node_modules, **/bin)
}

/// <summary>
/// The value of a list field, a list of texts. A form has it on one line between commas, <c>**/bin, **/obj</c>, a
/// settings page a line an item with its own buttons.
/// </summary>
public static class UIFormList
{
    /// <summary>The items of a value in order, nothing for a value that isn't a list. An item that isn't a text is written the way it reads.</summary>
    public static List<string> Of(IRuntimeValue? value)
    {
        var items = new List<string>();
        if (value is ListValue list)
        {
            foreach (IRuntimeValue item in list.Items)
                items.Add(item is StringValue text ? text.Value : item.ToString() ?? string.Empty);
        }

        return items;
    }

    /// <summary>The items as a value, trimmed, the empty ones left out and each kept once.</summary>
    public static ListValue Value(IEnumerable<string> items) =>
        new([.. items.Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).Select(item => (IRuntimeValue)new StringValue(item))]);

    /// <summary>The items on one line the way a form shows them, <c>**/bin, **/obj</c>.</summary>
    public static string Line(IRuntimeValue? value) => string.Join(", ", Of(value));

    /// <summary>A line of items read back, each between commas.</summary>
    public static ListValue Parse(string line) => Value(line.Split(','));
}

/// <summary>
/// The value of a field of pairs, an object of texts by text, read and written as a person types it. A form has it on one
/// line, <c>*.orig = csharp, *.log = plaintext</c>, a settings page a line a pair with its own buttons.
/// </summary>
public static class UIFormPairs
{
    /// <summary>The pairs of a value in the order it has them, nothing for a value that isn't an object. A value that isn't a text is written the way it reads.</summary>
    public static List<(string Key, string Value)> Of(IRuntimeValue? value)
    {
        var pairs = new List<(string, string)>();
        if (value is ObjectValue { Properties: { } properties })
        {
            foreach (var (key, item) in properties)
                pairs.Add((key, item is StringValue text ? text.Value : item.ToString() ?? string.Empty));
        }

        return pairs;
    }

    /// <summary>The pairs as a value, the last of two with the same key winning and the ones with no key left out.</summary>
    public static ObjectValue Value(IEnumerable<(string Key, string Value)> pairs)
    {
        var properties = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            string named = key.Trim();
            if (named.Length > 0)
                properties[named] = new StringValue(value.Trim());
        }

        return new ObjectValue(properties);
    }

    /// <summary>The pairs on one line the way a form shows them, <c>*.orig = csharp, *.log = plaintext</c>.</summary>
    public static string Line(IRuntimeValue? value) => string.Join(", ", Of(value).Select(pair => $"{pair.Key} = {pair.Value}"));

    /// <summary>A line of pairs read back, each pair between commas and its key before the first equals sign. A pair with no equals sign is a key with nothing for its value.</summary>
    public static ObjectValue Parse(string line) => Value(line.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part =>
    {
        int equals = part.IndexOf('=');
        return equals < 0 ? (part, string.Empty) : (part[..equals], part[(equals + 1)..]);
    }));
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
            throw new HidlRuntimeException($"'{kind}' isn't a kind of field, number, whole, flag, text, choice, flags, vec, pairs or list.");

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
