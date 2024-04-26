namespace Horizon.Horlang.Parsing;

public readonly struct AssignmentExpression(in IExpression assignee, in IExpression value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.Assignment;
    public IExpression Assignee { get; init; } = assignee;
    public IExpression Value { get; init; } = value;
}