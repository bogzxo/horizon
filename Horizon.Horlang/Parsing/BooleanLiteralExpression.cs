namespace Horizon.Horlang.Parsing;

public readonly struct BooleanLiteralExpression(in bool value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.BooleanLiteral;
    public bool Value { get; init; } = value;

    public override string ToString() => Value.ToString();
}