namespace Horizon.Horlang.Runtime;

public readonly struct NullValue() : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.Null;

    public override string ToString()
    {
        return "null";
    }
}
