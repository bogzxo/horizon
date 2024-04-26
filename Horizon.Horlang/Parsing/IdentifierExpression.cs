namespace Horizon.Horlang.Parsing;

public readonly struct IdentifierExpression(in string symbol) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.Identifier;
    public readonly string Symbol { get; init; } = symbol;

    public override string ToString()
    {
        return $"[{Type}] Symbol: {Symbol}";
    }
}