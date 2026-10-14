namespace Horizon.HIDL.Parsing;

public readonly struct VectorDeclarationExpression(params IExpression[] valExpressions) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.VectorDeclaration;
    public readonly IExpression[] Expressions { get; init; } = valExpressions;
}