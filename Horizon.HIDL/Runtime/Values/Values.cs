using System.Globalization;
using System.Numerics;

namespace Horizon.HIDL.Runtime;

/// <summary>
/// What every value has in common. How it reads as text, whether it counts as true, and when two are the same.
/// The interpreter and the library both go by these, so a number prints the same everywhere.
/// </summary>
public static class Values
{
    public static readonly NullValue Null = new();
    public static readonly BooleanValue True = new(true);
    public static readonly BooleanValue False = new(false);

    public static BooleanValue Bool(bool value) => value ? True : False;

    /// <summary>
    /// A number as text. Whole numbers without a point, the rest with as many digits as it takes and no more.
    /// </summary>
    public static string Text(float number) => number.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Any value as text, the way text(value) and string joining see it.
    /// </summary>
    public static string Text(IRuntimeValue? value) => value switch
    {
        null or NullValue => "null",
        StringValue text => text.Value ?? string.Empty,
        NumberValue number => Text(number.Value),
        BooleanValue boolean => boolean.Value ? "true" : "false",
        NativeValue native => Text(native.AccessorCallback()),
        NativeFunctionValue => "func",
        FunctionValue function => $"func {function.Name}",
        AnonymousFunctionValue => "func",
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>
    /// Whether a value counts as true in an if or a loop. Null, false, zero and an empty text or list don't, everything else does.
    /// </summary>
    public static bool IsTruthy(IRuntimeValue? value) => value switch
    {
        null or NullValue => false,
        BooleanValue boolean => boolean.Value,
        NumberValue number => number.Value != 0.0f,
        StringValue text => !string.IsNullOrEmpty(text.Value),
        ListValue list => list.Count > 0,
        NativeValue native => IsTruthy(native.AccessorCallback()),
        _ => true
    };

    /// <summary>
    /// Whether two values are the same as far as == goes. Numbers, texts, booleans and vectors by what they hold,
    /// everything else by being the very same thing.
    /// </summary>
    public static bool AreEqual(IRuntimeValue? a, IRuntimeValue? b)
    {
        if (a is NativeValue nativeA) a = nativeA.AccessorCallback();
        if (b is NativeValue nativeB) b = nativeB.AccessorCallback();

        return (a, b) switch
        {
            (null or NullValue, null or NullValue) => true,
            (NumberValue x, NumberValue y) => x.Value == y.Value,
            (StringValue x, StringValue y) => x.Value == y.Value,
            (BooleanValue x, BooleanValue y) => x.Value == y.Value,
            (Vector2Value x, Vector2Value y) => x.Value == y.Value,
            (Vector3Value x, Vector3Value y) => x.Value == y.Value,
            (Vector4Value x, Vector4Value y) => x.Value == y.Value,
            (ObjectValue x, ObjectValue y) => ReferenceEquals(x.Properties, y.Properties),
            (ListValue x, ListValue y) => ReferenceEquals(x, y),
            _ => false
        };
    }

    /// <summary>
    /// What a value is called in an error message and by typeof(). "number", "text", "list" and so on.
    /// </summary>
    public static string TypeName(IRuntimeValue? value) => value switch
    {
        null or NullValue => "null",
        NumberValue => "number",
        StringValue => "text",
        BooleanValue => "boolean",
        ObjectValue => "object",
        ListValue => "list",
        Vector2Value or Vector3Value or Vector4Value => "vector",
        NativeFunctionValue or FunctionValue or AnonymousFunctionValue => "function",
        NativeValue native => TypeName(native.AccessorCallback()),
        _ => value.Type.ToString().ToLowerInvariant()
    };

    public static bool IsFunction(IRuntimeValue? value) => value is NativeFunctionValue or FunctionValue or AnonymousFunctionValue;

    /// <summary>
    /// The components of a vector of any size, for the maths that works on all of them.
    /// </summary>
    public static bool TryGetComponents(IRuntimeValue value, Span<float> into, out int count)
    {
        switch (value)
        {
            case Vector2Value v:
                into[0] = v.Value.X; into[1] = v.Value.Y;
                count = 2;
                return true;
            case Vector3Value v:
                into[0] = v.Value.X; into[1] = v.Value.Y; into[2] = v.Value.Z;
                count = 3;
                return true;
            case Vector4Value v:
                into[0] = v.Value.X; into[1] = v.Value.Y; into[2] = v.Value.Z; into[3] = v.Value.W;
                count = 4;
                return true;
            default:
                count = 0;
                return false;
        }
    }

    /// <summary>
    /// A vector out of components, two to four of them.
    /// </summary>
    public static IRuntimeValue Vector(ReadOnlySpan<float> components) => components.Length switch
    {
        2 => new Vector2Value(new Vector2(components[0], components[1])),
        3 => new Vector3Value(new Vector3(components[0], components[1], components[2])),
        4 => new Vector4Value(new Vector4(components[0], components[1], components[2], components[3])),
        _ => throw new HidlRuntimeException($"A vector has 2, 3 or 4 numbers, not {components.Length}.")
    };
}

/// <summary>
/// Something went wrong while a program ran. The message says what in plain words, for whoever wrote the script.
/// </summary>
public class HidlRuntimeException(string message) : Exception(message);
