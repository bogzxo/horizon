namespace Horizon.Horlang.Parsing;

public readonly struct StringLiteralExpression(in string value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.StringLiteral;
    public string Value { get; init; } = value;

    public override string ToString() => Value;
}