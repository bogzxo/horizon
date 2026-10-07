using System.Globalization;

using Horizon.HIDL.Runtime;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.HIDL.Library;

/// <summary>
/// What a text can do. "abc".upper(), text.length, text.split(",") and the rest hang off the text itself.
/// </summary>
public static class TextLibrary
{
    /// <summary>
    /// text.something, for the interpreter.
    /// </summary>
    public static IRuntimeValue Member(string text, string name) => name switch
    {
        "length" or "count" => new NumberValue(text.Length),
        "upper" => Natives.Function("upper", _ => new StringValue(text.ToUpperInvariant())),
        "lower" => Natives.Function("lower", _ => new StringValue(text.ToLowerInvariant())),
        "trim" => Natives.Function("trim", _ => new StringValue(text.Trim())),
        "contains" => Natives.Function("contains", args => Values.Bool(text.Contains(args.Text(0), StringComparison.Ordinal))),
        "starts_with" => Natives.Function("starts_with", args => Values.Bool(text.StartsWith(args.Text(0), StringComparison.Ordinal))),
        "ends_with" => Natives.Function("ends_with", args => Values.Bool(text.EndsWith(args.Text(0), StringComparison.Ordinal))),
        "index_of" => Natives.Function("index_of", args => new NumberValue(text.IndexOf(args.Text(0), StringComparison.Ordinal))),
        "replace" => Natives.Function("replace", args => new StringValue(text.Replace(args.Text(0), args.Text(1)))),
        "split" => Natives.Function("split", args =>
        {
            // Split on a separator, trimming the pieces. "a, b" split on "," is ["a", "b"]
            string separator = args.Text(0, ",");
            return Natives.List(text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }),
        "substring" => Natives.Function("substring", args =>
        {
            int from = Math.Clamp(args.Integer(0), 0, text.Length);
            int to = Math.Clamp(args.Integer(1, text.Length), from, text.Length);
            return new StringValue(text[from..to]);
        }),
        "pad_left" => Natives.Function("pad_left", args => new StringValue(text.PadLeft(args.Integer(0), args.Text(1, " ")[0]))),
        "pad_right" => Natives.Function("pad_right", args => new StringValue(text.PadRight(args.Integer(0), args.Text(1, " ")[0]))),
        "repeat" => Natives.Function("repeat", args => new StringValue(string.Concat(Enumerable.Repeat(text, Math.Max(0, args.Integer(0)))))),

        _ => throw new HidlRuntimeException($"A text has no '{name}'. It has length, upper, lower, trim, contains, starts_with, ends_with, index_of, replace, split, substring, pad_left, pad_right and repeat.")
    };

    /// <summary>
    /// The functions about texts and types that stand on their own. text(x), number(x), typeof(x), len(x) and format(x, digits).
    /// </summary>
    public static void Declare(Environment scope)
    {
        scope.Declare("text", Natives.Function("text", args =>
        {
            // Everything handed over, joined. text(a, " ", b) is a cheap way to build a line
            if (args.Count <= 1) return new StringValue(Values.Text(args[0]));

            var pieces = new string[args.Count];
            for (int i = 0; i < pieces.Length; i++) pieces[i] = Values.Text(args[i]);
            return new StringValue(string.Concat(pieces));
        }), true);

        scope.Declare("number", Natives.Function("number", args =>
        {
            switch (args[0])
            {
                case NumberValue number:
                    return number;
                case BooleanValue boolean:
                    return new NumberValue(boolean.Value ? 1.0f : 0.0f);
                case StringValue text when float.TryParse(text.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed):
                    return new NumberValue(parsed);
                default:
                    // What can't be read as a number comes back as whatever the caller said, null if they said nothing
                    return args.Has(1) ? args[1] : Values.Null;
            }
        }), true);

        scope.Declare("format", Natives.Function("format", args =>
        {
            // A number with so many digits after the point, for a HUD that shouldn't jitter
            int digits = Math.Clamp(args.Integer(1, 2), 0, 9);
            return new StringValue(args.Number(0).ToString("F" + digits, CultureInfo.InvariantCulture));
        }), true);

        scope.Declare("typeof", Natives.Function("typeof", args => new StringValue(Values.TypeName(args[0]))), true);

        scope.Declare("len", Natives.Function("len", args => args[0] switch
        {
            ListValue list => new NumberValue(list.Count),
            StringValue text => new NumberValue(text.Value?.Length ?? 0),
            ObjectValue obj => new NumberValue(obj.Properties.Count),
            _ => throw args.Wrong($"a {Values.TypeName(args[0])} has no length.")
        }), true);
    }
}
