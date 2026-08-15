using Horizon.HIDL.Parsing;

namespace Horizon.HIDL.Parsing;

public class YieldStatement : IStatement
{
    public YieldStatement(IExpression expr)
    {
        Expression = expr;
        Type = NodeType.YieldStatement;
    }

    public IExpression Expression { get; init; }

    public NodeType Type { get; init; }
}