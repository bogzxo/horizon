﻿using System.Text;

namespace Horizon.Horlang.Parsing;

public readonly struct ProgramStatement() : IStatement
{
    public NodeType Type { get; init; } = NodeType.Program;
    public List<IStatement> Body { get; init; } = [];

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"[{Type}]");
        foreach (var smnt in Body)
            sb.AppendLine(smnt.ToString());
        return sb.ToString();
    }
}