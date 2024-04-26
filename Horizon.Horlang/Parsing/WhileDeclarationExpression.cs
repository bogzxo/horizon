namespace Horizon.Horlang.Parsing;

public readonly struct WhileDeclarationExpression(in IExpression expression, in IStatement[] body) : IExpression
{
    public NodeType Type { get; init; } = NodeType.WhileExpression;
    public IExpression Condition { get; init; } = expression;
    public IStatement[] Body { get; init; } = body;
}
