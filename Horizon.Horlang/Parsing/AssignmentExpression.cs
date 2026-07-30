namespace Horizon.HIDL.Parsing;

public readonly struct AssignmentExpression(in IExpression assignee, in IExpression value) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.Assignment;
    public readonly IExpression Assignee { get; init; } = assignee;
    public readonly IExpression Value { get; init; } = value;
}