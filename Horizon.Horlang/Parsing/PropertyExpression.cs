namespace Horizon.Horlang.Parsing;

public readonly struct PropertyExpression(in string key, in IExpression? value) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.Property;
    public readonly string Key { get; init; } = key;
    public readonly IExpression? Value { get; init; } = value;

    public override string ToString()
    {
        return $"[{Type}] Caller: {Value}";
    }
}