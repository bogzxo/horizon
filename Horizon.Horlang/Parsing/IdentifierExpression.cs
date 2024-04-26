namespace Horizon.Horlang.Parsing;

public readonly struct IdentifierExpression(in string symbol) : IExpression
{
    public NodeType Type { get; init; } = NodeType.Identifier;
    public string Symbol { get; init; } = symbol;

    public override string ToString()
    {
        return $"[{Type}] Symbol: {Symbol}";
    }
}