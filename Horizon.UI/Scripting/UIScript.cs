using System.Numerics;

using Horizon.HIDL.Runtime;

namespace Horizon.UI.Scripting;

/// <summary>
/// Conversions between HIDL runtime values and the types the UI works with. Reading the wrong kind
/// of value throws with a message naming what was being set, which ends up in front of whoever
/// wrote the script.
/// </summary>
internal static class UIScript
{
    public static float ToNumber(IRuntimeValue value, string what) =>
        value is NumberValue number ? number.Value : throw new Exception($"{what} has to be a number.");

    public static bool ToBoolean(IRuntimeValue value, string what) =>
        value is BooleanValue boolean ? boolean.Value : throw new Exception($"{what} has to be true or false.");

    public static string ToText(IRuntimeValue value, string what) => value switch
    {
        StringValue text => text.Value,
        NumberValue number => number.ToString(),
        _ => throw new Exception($"{what} has to be a string.")
    };

    /// <summary>A list of texts, or one text with commas in it for the old layouts, "a, b, c".</summary>
    public static string[] ToTexts(IRuntimeValue value, string what) => value switch
    {
        ListValue list => ToTextsFromList(list, what),
        StringValue text => text.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => throw new Exception($"{what} has to be a list of texts.")
    };

    private static string[] ToTextsFromList(ListValue list, string what)
    {
        int count = list.Items.Count;
        if (count == 0)
            return [];

        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = ToText(list.Items[i], what);
        return result;
    }

    public static ListValue FromTexts(IEnumerable<string> texts)
    {
        var items = texts is ICollection<string> c
            ? new List<IRuntimeValue>(c.Count)
            : texts is IReadOnlyCollection<string> rc
                ? new List<IRuntimeValue>(rc.Count)
                : new List<IRuntimeValue>();
        foreach (string text in texts)
            items.Add(new StringValue(text));
        return new ListValue(items);
    }

    /// <summary>A vec(x, y), or a single number for both.</summary>
    public static Vector2 ToVector2(IRuntimeValue value, string what) => value switch
    {
        Vector2Value vector => vector.Value,
        NumberValue number => new Vector2(number.Value),
        _ => throw new Exception($"{what} has to be a vec(x, y) or a number.")
    };

    /// <summary>A vec(r, g, b, a), or a vec(r, g, b) that is fully opaque.</summary>
    public static Vector4 ToColor(IRuntimeValue value, string what) => value switch
    {
        Vector4Value color => color.Value,
        Vector3Value color => new Vector4(color.Value, 1.0f),
        _ => throw new Exception($"{what} has to be a vec(r, g, b, a) or a vec(r, g, b).")
    };

    /// <summary>A number for all four sides, vec(horizontal, vertical) or vec(left, top, right, bottom).</summary>
    public static UIEdges ToEdges(IRuntimeValue value, string what) => value switch
    {
        NumberValue number => new UIEdges(number.Value),
        Vector2Value vector => new UIEdges(vector.Value.X, vector.Value.Y),
        Vector4Value vector => new UIEdges(vector.Value.X, vector.Value.Y, vector.Value.Z, vector.Value.W),
        _ => throw new Exception($"{what} has to be a number, a vec(horizontal, vertical) or a vec(left, top, right, bottom).")
    };

    /// <summary>The name of an enum value, written the way scripts write names, "top_left" for TopLeft.</summary>
    public static T ToEnum<T>(IRuntimeValue value, string what) where T : struct, Enum
    {
        if (value is StringValue text)
        {
            if (EnumCache<T>.FromScript.TryGetValue(text.Value, out T cached))
                return cached;

            if (TryParseStripped(text.Value, out T parsed))
                return parsed;
        }

        throw new Exception($"{what} has to be one of: {EnumCache<T>.ValidNames}.");
    }

    public static StringValue FromEnum<T>(T value) where T : struct, Enum =>
        EnumCache<T>.ToScript.TryGetValue(value, out StringValue cached) ? cached : new StringValue(ToSnakeCase(value.ToString()));

    public static IRuntimeValue FromEdges(UIEdges edges) =>
        new Vector4Value(new Vector4(edges.Left, edges.Top, edges.Right, edges.Bottom));

    internal static string ToSnakeCase(string name)
    {
        if (name.Length == 0)
            return string.Empty;

        int extra = 0;
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
                extra++;
        }

        int total = name.Length + extra;
        return string.Create(total, name, static (dest, src) =>
        {
            int pos = 0;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (i > 0 && char.IsUpper(c))
                    dest[pos++] = '_';
                dest[pos++] = char.ToLowerInvariant(c);
            }
        });
    }

    private static bool TryParseStripped<T>(string raw, out T result) where T : struct, Enum
    {
        if (raw.IndexOf('_') < 0)
            return Enum.TryParse(raw.AsSpan(), ignoreCase: true, out result);

        if (raw.Length <= 64)
        {
            Span<char> buffer = stackalloc char[raw.Length];
            int len = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '_')
                    buffer[len++] = raw[i];
            }
            return Enum.TryParse(buffer[..len], ignoreCase: true, out result);
        }

        return Enum.TryParse(raw.Replace("_", string.Empty), ignoreCase: true, out result);
    }

    private static class EnumCache<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, StringValue> ToScript;
        public static readonly Dictionary<string, T> FromScript;
        public static readonly string ValidNames;

        static EnumCache()
        {
            T[] values = Enum.GetValues<T>();
            string[] names = Enum.GetNames<T>();
            ToScript = new Dictionary<T, StringValue>(values.Length);
            FromScript = new Dictionary<string, T>(values.Length * 2, StringComparer.OrdinalIgnoreCase);
            var valid = new string[names.Length];

            for (int i = 0; i < values.Length; i++)
            {
                string snake = ToSnakeCase(names[i]);
                valid[i] = snake;
                var sv = new StringValue(snake);
                ToScript.TryAdd(values[i], sv);
                FromScript.TryAdd(snake, values[i]);
                FromScript.TryAdd(names[i], values[i]);
            }

            ValidNames = string.Join(", ", valid);
        }
    }
}
