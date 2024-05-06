namespace Horizon.HIDL.Parsing;

public readonly struct DoWhileDeclarationExpression(in IExpression expression, in IStatement[] body) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.DoWhileExpression;
    public readonly IExpression Condition { get; init; } = expression;
    public readonly IStatement[] Body { get; init; } = body;
}