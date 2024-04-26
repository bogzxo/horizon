namespace Horizon.Horlang.Runtime;

public readonly struct BooleanValue(in bool val) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.Boolean;
    public bool Value { get; init; } = val;

    public override string ToString()
    {
        return Value.ToString();
    }
}