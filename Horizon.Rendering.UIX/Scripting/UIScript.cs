using System.Numerics;

using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.UIX.Scripting;

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

    /// <summary>The name of an enum value, written the way scripts write names: "top_left" for TopLeft.</summary>
    public static T ToEnum<T>(IRuntimeValue value, string what) where T : struct, Enum
    {
        if (value is StringValue text && Enum.TryParse(text.Value.Replace("_", string.Empty), true, out T result))
            return result;

        throw new Exception($"{what} has to be one of: {string.Join(", ", Enum.GetNames<T>().Select(ToSnakeCase))}.");
    }

    public static StringValue FromEnum<T>(T value) where T : struct, Enum =>
        new(ToSnakeCase(value.ToString()));

    public static IRuntimeValue FromEdges(UIEdges edges) =>
        new Vector4Value(new Vector4(edges.Left, edges.Top, edges.Right, edges.Bottom));

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
