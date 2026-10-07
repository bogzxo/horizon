namespace Horizon.HIDL.Parsing;

/// <summary>
/// <c>[a, b, c]</c>. An entry can be a spread of another list, see <see cref="SpreadExpression"/>.
/// </summary>
public readonly struct ListLiteralExpression(in IExpression[] items) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.ListLiteral;
    public readonly IExpression[] Items { get; init; } = items;
}

/// <summary>
/// <c>...other</c> inside of an object or a list literal, which puts everything of the other one in at that spot.
/// In an object it is a property with this as its value and no key.
/// </summary>
public readonly struct SpreadExpression(in IExpression source) : IExpression
{
    public readonly NodeType Type { get; init; } = NodeType.Spread;
    public readonly IExpression Source { get; init; } = source;
}

/// <summary>
/// <c>for (name in list) { }</c>. Walks a list, the keys of an object, the characters of a string or 0 up to a number.
/// </summary>
public readonly struct ForInStatement(in string variable, in IExpression source, in IStatement[] body) : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.ForInStatement;
    public readonly string Variable { get; init; } = variable;
    public readonly IExpression Source { get; init; } = source;
    public readonly IStatement[] Body { get; init; } = body;
}

/// <summary>
/// <c>return value;</c> or a bare <c>return;</c>, which hands null back.
/// </summary>
public readonly struct ReturnStatement(in IExpression? value) : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.ReturnStatement;
    public readonly IExpression? Value { get; init; } = value;
}

public readonly struct BreakStatement() : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.BreakStatement;
}

public readonly struct ContinueStatement() : IStatement
{
    public readonly NodeType Type { get; init; } = NodeType.ContinueStatement;
}
