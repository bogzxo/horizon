namespace Horizon.Horlang.Parsing;

public readonly struct NumericLiteralExpression(in int value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.NumericLiteral;
    public int Value { get; init; } = value;

    public override string ToString()
    {
        return $"[{Type}] Caller: {Value}";
    }
}
public readonly struct StringLiteralExpression(in string value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.StringLiteral;
    public string Value { get; init; } = value;

    public override string ToString() => Value;
}