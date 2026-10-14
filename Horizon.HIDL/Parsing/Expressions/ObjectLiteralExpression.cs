namespace Horizon.HIDL.Parsing;

public readonly struct ObjectLiteralExpression(in List<PropertyExpression> properties) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.ObjectLiteral;
    public readonly List<PropertyExpression> Properties { get; init; } = properties;

    public override string ToString()
    {
        return $"[{Type}]: {Properties.Count}";
    }
}