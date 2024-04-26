namespace Horizon.Horlang.Parsing;

public readonly struct IfDeclarationExpression(in IExpression expression, in IStatement[] body) : IExpression
{
    public NodeType Type { get; init; } = NodeType.IfExpression;
    public IExpression Condition { get; init; } = expression;
    public IStatement[] Body { get; init; } = body;
}
