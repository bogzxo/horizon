namespace Horizon.Horlang.Parsing;

public readonly struct CallExpression(in IExpression[] args, in IExpression caller) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.CallExpression;
    public readonly IExpression[] Arguments { get; init; } = args;
    public readonly IExpression Caller { get; init; } = caller;
}