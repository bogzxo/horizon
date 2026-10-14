namespace Horizon.HIDL.Parsing;

/// <summary>
/// One <c>key: value</c> of an object literal. Without a value it is the shorthand <c>{ key }</c>, which takes the
/// variable of that name. A key of null with a <see cref="SpreadExpression"/> as the value is <c>...other</c>.
/// </summary>
public readonly struct PropertyExpression(in string? key, in IExpression? value) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.Property;
    public readonly string? Key { get; init; } = key;
    public readonly IExpression? Value { get; init; } = value;

    public override string ToString()
    {
        return $"[{Type}] {Key}: {Value}";
    }
}
