namespace Horizon.Horlang.Parsing;

public readonly struct PropertyExpression(in string key, in IExpression? value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.Property;
    public string Key { get; init; } = key;
    public IExpression? Value { get; init; } = value;

    public override string ToString()
    {
        return $"[{Type}] Caller: {Value}";
    }
}