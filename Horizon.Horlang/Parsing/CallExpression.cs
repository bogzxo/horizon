namespace Horizon.Horlang.Parsing;

public readonly struct CallExpression(in IExpression[] args, in IExpression caller) : IExpression
{
    public NodeType Type { get; init; } = NodeType.CallExpression;
    public IExpression[] Arguments { get; init; } = args;
    public IExpression Caller { get; init; } = caller;
}