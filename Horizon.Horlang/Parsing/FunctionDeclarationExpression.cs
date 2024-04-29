using System.Numerics;

namespace Horizon.HIDL.Parsing;

public readonly struct FunctionDeclarationExpression(in string name, in string[] parameters, in IStatement[] body) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.FunctionDeclaration;
    public readonly string Name { get; init; } = name;
    public readonly string[] Parameters { get; init; } = parameters;
    public readonly IStatement[] Body { get; init; } = body;
}
