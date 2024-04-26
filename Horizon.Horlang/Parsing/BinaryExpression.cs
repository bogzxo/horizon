namespace Horizon.Horlang.Parsing;

public readonly struct BinaryExpression(in IExpression left, in IExpression right, in string op) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.BinaryExpression;
    public readonly IExpression Left { get; init; } = left;
    public readonly IExpression Right { get; init; } = right;
    public readonly string Operator { get; init; } = op;

    public override string ToString()
    {
        return $"[{Type}] Left: ({Left.ToString()}\t{Operator}\t{Right.ToString()})";
    }
}