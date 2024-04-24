namespace Horizon.Horlang.Parsing;

public readonly struct ObjectLiteralExpression(in List<PropertyExpression> properties) : IExpression
{
    public NodeType Type { get; init; } = NodeType.ObjectLiteral;
    public List<PropertyExpression> Properties { get; init; } = properties;

    public override string ToString()
    {
        return $"[{Type}]: {Properties.Count}";
    }
}
