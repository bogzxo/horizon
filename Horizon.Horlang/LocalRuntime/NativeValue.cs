using System.Numerics;
using System.Runtime.InteropServices;

namespace Horizon.HIDL.Runtime;

public readonly struct NativeValue(in Func<IRuntimeValue> accessorCallback, in Action<IRuntimeValue> mutatorCallback) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.NativeValue;
    public readonly Func<IRuntimeValue> AccessorCallback { get; init; } = accessorCallback;
    public readonly Action<IRuntimeValue> MutatorCallback { get; init; } = mutatorCallback;
}

public readonly struct Vector2Value(in Vector2 value) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Vector2;
    public readonly Vector2 Value { get; init; } = value;
}

public readonly struct Vector3Value(in Vector3 value) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Vector3;
    public readonly Vector3 Value { get; init; } = value;
}

public readonly struct Vector4Value(in Vector4 value) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Vector4;
    public readonly Vector4 Value { get; init; } = value;
}