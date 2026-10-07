namespace Horizon.HIDL.Parsing;

/// <summary>
/// <c>if (condition) { } else if (other) { } else { }</c>. An else-if chain is an if inside of the else.
/// </summary>
public readonly struct IfDeclarationExpression(in IExpression expression, in IStatement[] body, in IStatement[]? otherwise = null) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.IfExpression;
    public readonly IExpression Condition { get; init; } = expression;
    public readonly IStatement[] Body { get; init; } = body;

    /// <summary>What runs when the condition doesn't hold, null for nothing.</summary>
    public readonly IStatement[]? Else { get; init; } = otherwise;
}
