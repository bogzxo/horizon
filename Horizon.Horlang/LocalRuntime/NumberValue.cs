namespace Horizon.Horlang.Runtime;

public readonly struct NumberValue(in float val) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.Number;
    public float Value { get; init; } = val;

    public override string ToString()
    {
        return Value.ToString();
    }
}
