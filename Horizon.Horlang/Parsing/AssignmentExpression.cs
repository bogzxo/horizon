namespace Horizon.Horlang.Parsing;

public readonly struct AssignmentExpression(in IExpression assignee, in IExpression value) : IExpression
{
    public NodeType Type { get; init; } = NodeType.Assignment;
    public IExpression Assignee { get; init; } = assignee;
    public IExpression Value { get; init; } = value;
}
public struct MemberExpression(in IExpression obj, in IExpression property, in bool computed) : IExpression
{
    public NodeType Type { get; init; } = NodeType.MemberExpression;
    public IExpression Object { get; init; } = obj;
    public IExpression Property { get; init; } = property;
    public bool Computed { get; set; } = computed;
}
public readonly struct CallExpression(in IExpression[] args, in IExpression caller) : IExpression
{
    public NodeType Type { get; init; } = NodeType.CallExpression;
    public IExpression[] Arguments { get; init; } = args;
    public IExpression Caller { get; init; } = caller;
}
