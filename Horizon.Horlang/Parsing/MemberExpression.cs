namespace Horizon.Horlang.Parsing;

public struct MemberExpression(in IExpression obj, in IExpression property, in bool computed) : IExpression
{
    public NodeType Type { get; init; } = NodeType.MemberExpression;
    public IExpression Object { get; init; } = obj;
    public IExpression Property { get; init; } = property;
    public bool Computed { get; set; } = computed;
}
