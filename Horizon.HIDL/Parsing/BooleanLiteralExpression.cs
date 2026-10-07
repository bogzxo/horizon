namespace Horizon.HIDL.Parsing;

public readonly struct BooleanLiteralExpression(in bool value) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.BooleanLiteral;
    public readonly bool Value { get; init; } = value;

    public override string ToString() => Value.ToString();
}