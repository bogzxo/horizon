using System.Diagnostics.CodeAnalysis;

namespace Horizon.HIDL.Runtime;

public readonly struct NumberValue(in float val) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Number;
    public readonly float Value { get; init; } = val;

    public override string ToString() => Values.Text(Value);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is NumberValue other && other.Value == Value;

    public override int GetHashCode() => Value.GetHashCode();
}
