namespace Horizon.HIDL.Parsing;

public readonly struct NullLiteral() : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.NullLiteral;

    public override string ToString()
    {
        return "null";
    }
}