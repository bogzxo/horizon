namespace Horizon.HIDL.Parsing;

public readonly struct DeleteStatement(in string target) : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.DeleteStatement;
    public readonly string Target { get; init; } = target;
}