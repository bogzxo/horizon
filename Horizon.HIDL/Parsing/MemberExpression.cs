namespace Horizon.HIDL.Parsing;

public struct MemberExpression(in IExpression obj, in IExpression property, in bool computed) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.MemberExpression;
    public readonly IExpression Object { get; init; } = obj;
    public readonly IExpression Property { get; init; } = property;
    public readonly bool Computed { get; init; } = computed;
}