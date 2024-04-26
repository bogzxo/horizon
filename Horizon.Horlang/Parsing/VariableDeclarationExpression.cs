namespace Horizon.Horlang.Parsing;

public readonly struct VariableDeclarationExpression(in string identifier, in IExpression? value, in bool constant) : IExpression
{
    public NodeType Type { get; init; } = NodeType.VariableDeclaration;
    public string Identifier { get; init; } = identifier;
    public IExpression? Value { get; init; } = value;
    public bool ReadOnly { get; init; } = constant;
}