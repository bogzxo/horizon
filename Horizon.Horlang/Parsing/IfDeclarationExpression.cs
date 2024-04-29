namespace Horizon.HIDL.Parsing;

public readonly struct IfDeclarationExpression(in IExpression expression, in IStatement[] body) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.IfExpression;
    public readonly IExpression Condition { get; init; } = expression;
    public readonly IStatement[] Body { get; init; } = body;
}