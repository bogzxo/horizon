using Horizon.HIDL.Runtime;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.HIDL.Library;

/// <summary>
/// What a list can do. list.push(x), list.pop(), list.count and the rest hang off the list itself, the way a
/// script expects. The members are made on the spot, a list carries nothing but its items.
/// </summary>
public static class ListLibrary
{
    /// <summary>
    /// list.something, for the interpreter.
    /// </summary>
    public static IRuntimeValue Member(ListValue list, string name) => name switch
    {
        "count" or "length" => new NumberValue(list.Count),
        "first" => list.Count > 0 ? list[0] : Values.Null,
        "last" => list.Count > 0 ? list[^1] : Values.Null,

        "push" => Natives.Function("push", args =>
        {
            foreach (IRuntimeValue value in args.All) list.Items.Add(value);
            return list;
        }),
        "pop" => Natives.Function("pop", _ =>
        {
            if (list.Count == 0) return Values.Null;

            IRuntimeValue last = list[^1];
            list.Items.RemoveAt(list.Count - 1);
            return last;
        }),
        "insert" => Natives.Function("insert", args =>
        {
            int index = Math.Clamp(args.Integer(0), 0, list.Count);
            list.Items.Insert(index, args[1]);
            return list;
        }),
        "remove" => Natives.Function("remove", args =>
        {
            int index = list.Items.FindIndex(item => Values.AreEqual(item, args[0]));
            if (index >= 0) list.Items.RemoveAt(index);
            return Values.Bool(index >= 0);
        }),
        "remove_at" => Natives.Function("remove_at", args =>
        {
            int index = args.Integer(0);
            if (index < 0 || index >= list.Count) throw args.Wrong($"there is no item {index} in a list of {list.Count}.");

            IRuntimeValue removed = list[index];
            list.Items.RemoveAt(index);
            return removed;
        }),
        "clear" => Natives.Function("clear", _ =>
        {
            list.Items.Clear();
            return list;
        }),
        "contains" => Natives.Function("contains", args => Values.Bool(list.Items.Exists(item => Values.AreEqual(item, args[0])))),
        "index_of" => Natives.Function("index_of", args => new NumberValue(list.Items.FindIndex(item => Values.AreEqual(item, args[0])))),
        "join" => Natives.Function("join", args =>
        {
            string separator = args.Text(0, ", ");
            return new StringValue(string.Join(separator, list.Items.Select(Values.Text)));
        }),
        "reverse" => Natives.Function("reverse", _ =>
        {
            list.Items.Reverse();
            return list;
        }),
        "copy" => Natives.Function("copy", _ => new ListValue([.. list.Items])),
        "slice" => Natives.Function("slice", args =>
        {
            int from = Math.Clamp(args.Integer(0, 0), 0, list.Count);
            int to = Math.Clamp(args.Integer(1, list.Count), from, list.Count);
            return new ListValue(list.Items.GetRange(from, to - from));
        }),
        "sort" => Natives.Function("sort", args =>
        {
            // By what the items are, or by what a function says about each of them
            if (args.Has(0))
            {
                IRuntimeValue key = args.Callable(0);
                var runtime = HIDLRuntime.Current ?? throw args.Wrong("sorting with a function needs a running program.");
                list.Items.Sort((a, b) => Compare(runtime.Interpreter.Call(key, [a], args.Scope), runtime.Interpreter.Call(key, [b], args.Scope)));
            }
            else
            {
                list.Items.Sort(Compare);
            }

            return list;
        }),
        "map" => Natives.Function("map", args =>
        {
            IRuntimeValue function = args.Callable(0);
            var runtime = HIDLRuntime.Current ?? throw args.Wrong("map needs a running program.");
            return new ListValue([.. list.Items.Select(item => runtime.Interpreter.Call(function, [item], args.Scope))]);
        }),
        "filter" => Natives.Function("filter", args =>
        {
            IRuntimeValue function = args.Callable(0);
            var runtime = HIDLRuntime.Current ?? throw args.Wrong("filter needs a running program.");
            return new ListValue([.. list.Items.Where(item => Values.IsTruthy(runtime.Interpreter.Call(function, [item], args.Scope)))]);
        }),
        "any" => Natives.Function("any", args =>
        {
            IRuntimeValue function = args.Callable(0);
            var runtime = HIDLRuntime.Current ?? throw args.Wrong("any needs a running program.");
            return Values.Bool(list.Items.Any(item => Values.IsTruthy(runtime.Interpreter.Call(function, [item], args.Scope))));
        }),
        "all" => Natives.Function("all", args =>
        {
            IRuntimeValue function = args.Callable(0);
            var runtime = HIDLRuntime.Current ?? throw args.Wrong("all needs a running program.");
            return Values.Bool(list.Items.All(item => Values.IsTruthy(runtime.Interpreter.Call(function, [item], args.Scope))));
        }),

        _ => throw new HidlRuntimeException($"A list has no '{name}'. It has count, first, last, push, pop, insert, remove, remove_at, clear, contains, index_of, join, reverse, copy, slice, sort, map, filter, any and all.")
    };

    /// <summary>
    /// How two values are ordered by sort. Numbers by size, texts alphabetically, everything else stays put.
    /// </summary>
    public static int Compare(IRuntimeValue a, IRuntimeValue b) => (a, b) switch
    {
        (NumberValue x, NumberValue y) => x.Value.CompareTo(y.Value),
        (StringValue x, StringValue y) => string.CompareOrdinal(x.Value, y.Value),
        (BooleanValue x, BooleanValue y) => x.Value.CompareTo(y.Value),
        _ => 0
    };

    /// <summary>
    /// What a for loop walks. The items of a list, the keys of an object, the characters of a text, or 0 up to a number.
    /// </summary>
    public static IEnumerable<IRuntimeValue> Walk(IRuntimeValue source)
    {
        switch (source)
        {
            case ListValue list:
                // Over a copy, so the loop can change the list without tripping over itself
                foreach (IRuntimeValue item in list.Items.ToArray()) yield return item;
                break;

            case ObjectValue obj:
                foreach (string key in obj.Properties.Keys.ToArray()) yield return new StringValue(key);
                break;

            case StringValue text:
                foreach (char c in text.Value ?? string.Empty) yield return new StringValue(c.ToString());
                break;

            case NumberValue number:
                for (int i = 0; i < (int)number.Value; i++) yield return new NumberValue(i);
                break;

            default:
                throw new HidlRuntimeException($"A for loop can walk a list, an object, a text or a number, not {Values.TypeName(source)}.");
        }
    }
}
