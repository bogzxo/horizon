namespace Horizon.Horlang.Lexxing;

public readonly struct Token(in TokenType type, in string value)
{
    public TokenType Type { get; init; } = type;
    public string Value { get; init; } = value;

    public override string ToString()
    {
        return $"[{Type}, '{Value}']";
    }
}