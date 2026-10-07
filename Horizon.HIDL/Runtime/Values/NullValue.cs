namespace Horizon.HIDL.Runtime;

public readonly struct NullValue() : IRuntimeValue
{
    public readonly ValueType Type { get; init; } = ValueType.Null;

    public override string ToString()
    {
        return "null";
    }
}