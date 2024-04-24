namespace Horizon.Horlang.Parsing;

public readonly struct NullLiteral() : IExpression
{
    public NodeType Type { get; init; } = NodeType.NullLiteral;

    public override string ToString()
    {
        return "null";
    }
}
