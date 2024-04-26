namespace Horizon.Horlang.Parsing;

public readonly struct StringLiteralExpression(in string value) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.StringLiteral;
    public readonly string Value { get; init; } = value;

    public override string ToString() => Value;
}