namespace Horizon.Horlang.Parsing;

public readonly struct FunctionDeclarationExpression(in string name, in string[] parameters, in IStatement[] body) : IExpression
{
    public NodeType Type { get; init; } = NodeType.FunctionDeclaration;
    public string Name { get; init; } = name;
    public string[] Parameters { get; init; } = parameters;
    public IStatement[] Body { get; init; } = body;
}