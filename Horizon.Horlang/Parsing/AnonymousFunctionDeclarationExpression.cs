namespace Horizon.HIDL.Parsing;

public readonly struct AnonymousFunctionDeclarationExpression(in string[] parameters, in IStatement[] body) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.AnonymousFunctionDeclaration;
    public readonly string[] Parameters { get; init; } = parameters;
    public readonly IStatement[] Body { get; init; } = body;
}
