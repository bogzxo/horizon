namespace Horizon.HIDL.Parsing;

public readonly struct VariableDeclarationExpression(in string identifier, in IExpression? value, in bool constant) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.VariableDeclaration;
    public readonly string Identifier { get; init; } = identifier;
    public readonly IExpression? Value { get; init; } = value;
    public readonly bool ReadOnly { get; init; } = constant;
}