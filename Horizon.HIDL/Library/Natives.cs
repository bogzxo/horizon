using System.Numerics;

using Horizon.HIDL.Runtime;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.HIDL.Library;

/// <summary>
/// The quick way for a host to hand functions and objects to scripts. Every argument is checked and a wrong one
/// throws with the name of the function and what it wanted, so nobody has to write that over and over.
/// <code>
/// scope.DeclareSystem("fight", Natives.Object(
///     ("health_of", Natives.Function("health_of", args => new NumberValue(PlayerOf(args.Number(0)).Health))),
///     ("round", new NumberValue(round))));
/// </code>
/// </summary>
public static class Natives
{
    /// <summary>
    /// A function scripts can call. The body gets the arguments wrapped up with checks, see <see cref="Arguments"/>.
    /// </summary>
    public static NativeFunctionValue Function(string name, Func<Arguments, IRuntimeValue> body) =>
        new((args, env) => body(new Arguments(name, args, env)));

    /// <summary>
    /// A function that hands nothing back.
    /// </summary>
    public static NativeFunctionValue Action(string name, Action<Arguments> body) =>
        new((args, env) =>
        {
            body(new Arguments(name, args, env));
            return Values.Null;
        });

    /// <summary>
    /// An object with the members it is given, functions and values alike.
    /// </summary>
    public static ObjectValue Object(params (string Name, IRuntimeValue Value)[] members)
    {
        Dictionary<string, IRuntimeValue> properties = [];
        foreach (var (name, value) in members)
            properties[name] = value;

        return new ObjectValue(properties);
    }

    /// <summary>
    /// A value the host owns, read and written through callbacks.
    /// </summary>
    public static NativeValue Property(Func<IRuntimeValue> get, Action<IRuntimeValue>? set = null) =>
        new(get, set ?? (_ => throw new HidlRuntimeException("That can be read but not changed.")));

    /// <summary>
    /// A list out of texts, for handing a script the names of things.
    /// </summary>
    public static ListValue List(IEnumerable<string> texts)
    {
        var list = new ListValue();
        foreach (string text in texts)
            list.Items.Add(new StringValue(text));

        return list;
    }

    /// <summary>
    /// A list out of numbers.
    /// </summary>
    public static ListValue List(IEnumerable<float> numbers)
    {
        var list = new ListValue();
        foreach (float number in numbers)
            list.Items.Add(new NumberValue(number));

        return list;
    }
}

/// <summary>
/// The arguments of a native function as they were handed over, with a way to read each as what it has to be.
/// A missing or wrong argument throws with the name of the function, unless a fallback was given for it.
/// </summary>
public readonly struct Arguments(string function, IRuntimeValue[] values, Environment scope)
{
    /// <summary>The name of the function, for the messages.</summary>
    public string Function { get; } = function;

    /// <summary>The scope the function was called from.</summary>
    public Environment Scope { get; } = scope;

    public int Count => values.Length;

    public IRuntimeValue this[int index] => index < values.Length ? Unwrap(values[index]) : Values.Null;

    /// <summary>Whether an argument was given at all.</summary>
    public bool Has(int index) => index < values.Length && values[index] is not NullValue;

    /// <summary>Every argument, for functions that take any number of them.</summary>
    public ReadOnlySpan<IRuntimeValue> All => values;

    private static IRuntimeValue Unwrap(IRuntimeValue value) => value is NativeValue native ? native.AccessorCallback() : value;

    public HidlRuntimeException Wrong(string what) => new($"{Function}: {what}");

    private HidlRuntimeException Wanted(int index, string type) =>
        Wrong(index < values.Length
            ? $"argument {index + 1} has to be {type}, not {Values.TypeName(values[index])}."
            : $"argument {index + 1} is missing, it has to be {type}.");

    public float Number(int index) => this[index] is NumberValue number ? number.Value : throw Wanted(index, "a number");

    public float Number(int index, float fallback) => Has(index) ? Number(index) : fallback;

    public int Integer(int index) => (int)MathF.Round(Number(index));

    public int Integer(int index, int fallback) => Has(index) ? Integer(index) : fallback;

    public string Text(int index) => this[index] is StringValue text ? text.Value ?? string.Empty : throw Wanted(index, "a text");

    public string Text(int index, string fallback) => Has(index) ? Text(index) : fallback;

    public bool Bool(int index) => this[index] is BooleanValue boolean ? boolean.Value : throw Wanted(index, "true or false");

    public bool Bool(int index, bool fallback) => Has(index) ? Bool(index) : fallback;

    public ListValue List(int index) => this[index] is ListValue list ? list : throw Wanted(index, "a list");

    public ObjectValue Object(int index) => this[index] is ObjectValue obj ? obj : throw Wanted(index, "an object");

    public IRuntimeValue Callable(int index) => Values.IsFunction(this[index]) ? this[index] : throw Wanted(index, "a function");

    public Vector2 Vector2(int index) => this[index] is Vector2Value vector ? vector.Value : throw Wanted(index, "a vec(x, y)");

    public Vector3 Vector3(int index) => this[index] is Vector3Value vector ? vector.Value : throw Wanted(index, "a vec(x, y, z)");

    public Vector4 Vector4(int index) => this[index] is Vector4Value vector ? vector.Value : throw Wanted(index, "a vec(x, y, z, w)");

    /// <summary>
    /// A vector of any size, as its components. For maths that works the same on all of them.
    /// </summary>
    public int Components(int index, Span<float> into)
    {
        if (Values.TryGetComponents(this[index], into, out int count))
            return count;

        throw Wanted(index, "a vector");
    }
}
