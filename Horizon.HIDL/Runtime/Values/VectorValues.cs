using System.Numerics;

namespace Horizon.HIDL.Runtime;

// vec(x, y), vec(x, y, z) and vec(x, y, z, w). Positions, sizes and colours

public readonly struct Vector2Value(in Vector2 value) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Vector2;
    public readonly Vector2 Value { get; init; } = value;

    public override string ToString() => $"vec({Values.Text(Value.X)}, {Values.Text(Value.Y)})";
}

public readonly struct Vector3Value(in Vector3 value) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Vector3;
    public readonly Vector3 Value { get; init; } = value;

    public override string ToString() => $"vec({Values.Text(Value.X)}, {Values.Text(Value.Y)}, {Values.Text(Value.Z)})";
}

public readonly struct Vector4Value(in Vector4 value) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Vector4;
    public readonly Vector4 Value { get; init; } = value;

    public override string ToString() => $"vec({Values.Text(Value.X)}, {Values.Text(Value.Y)}, {Values.Text(Value.Z)}, {Values.Text(Value.W)})";
}
