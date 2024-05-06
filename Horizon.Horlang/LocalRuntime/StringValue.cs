namespace Horizon.HIDL.Runtime;

public readonly struct StringValue(in string str) : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.String;
    public readonly string Value { get; init; } = str;

    public override string ToString() => Value;
}