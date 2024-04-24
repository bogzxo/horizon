namespace Horizon.Horlang.Runtime;

public readonly struct StringValue(in string str) : IRuntimeValue
{
    public ValueType Type { get; init; } = ValueType.String;
    public string Value { get; init; } = str;

    public override string ToString() => Value;
}
