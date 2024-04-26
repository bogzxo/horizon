using System.Text;

namespace Horizon.Horlang.Parsing;

public readonly struct ProgramStatement() : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.Program;
    public readonly List<IStatement> Body { get; init; } = [];

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"[{Type}]");
        foreach (var smnt in Body)
            sb.AppendLine(smnt.ToString());
        return sb.ToString();
    }
}

public readonly struct DeleteStatement(in string target) : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.DeleteStatement;
    public readonly string Target { get; init; } = target;
}