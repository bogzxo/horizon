namespace Horizon.HIDL.Lexxing;

public readonly struct Token(in TokenType type, in string value, in int line = 1, in int col = 1)
{
    public TokenType Type { get; init; } = type;
    public string Value { get; init; } = value;
    public int Line { get; init; } = line;
    public int Col { get; init; } = col;

    public override string ToString()
    {
        return $"[{Type}, '{Value}']";
    }
}