namespace Horizon.HIDL.Parsing;

/// <summary>
/// <c>x = value</c>, or <c>x += value</c> and friends with the operator set.
/// </summary>
public readonly struct AssignmentExpression(in IExpression assignee, in IExpression value, in string? op = null) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.Assignment;
    public readonly IExpression Assignee { get; init; } = assignee;
    public readonly IExpression Value { get; init; } = value;

    /// <summary>The operator of a compound assignment ("+" for +=), null for a plain one.</summary>
    public readonly string? Operator { get; init; } = op;
}
