using System.Diagnostics.CodeAnalysis;

namespace Horizon.HIDL.Runtime;

public readonly struct NumberValue(in float val) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Number;
    public readonly float Value { get; init; } = val;

    public override string ToString()
    {
        return Value.ToString();
    }

    public override bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is NumberValue vally)
        {
            return vally.Value == this.Value;
        }
        throw new Exception();
    }
}