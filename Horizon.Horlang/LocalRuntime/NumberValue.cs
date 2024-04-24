namespace Horizon.Horlang.Runtime;

public readonly struct NumberValue(in int val) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.Number;
    public int Value { get; init; } = val;

    public override string ToString()
    {
        return Value.ToString();
    }
}
