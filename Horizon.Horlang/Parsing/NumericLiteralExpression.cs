namespace Horizon.Horlang.Parsing;

public readonly struct NumericLiteralExpression(in float value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.NumericLiteral;
    public float Value { get; init; } = value;

    public override string ToString()
    {
        return $"[{Type}] Caller: {Value}";
    }
}