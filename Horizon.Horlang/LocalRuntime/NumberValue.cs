namespace Horizon.HIDL.Runtime;

public readonly struct NumberValue(in float val) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Number;
    public readonly float Value { get; init; } = val;

    public override string ToString()
    {
        return Value.ToString();
    }
}