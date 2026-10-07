namespace Horizon.HIDL.Lexing;

/// <summary>
/// One piece of a program. What it is, what it says, and where it was in the source, which is what an error points
/// at and what an editor colours in.
/// </summary>
/// <param name="index">Where in the source it starts, in characters from the beginning.</param>
/// <param name="length">How many characters of source it is, quotes and all for a text.</param>
public readonly struct Token(in TokenType type, in string value, in int line = 1, in int col = 1, in int index = 0, in int length = 0)
{
    public TokenType Type { get; init; } = type;
    public string Value { get; init; } = value;
    public int Line { get; init; } = line;
    public int Col { get; init; } = col;
    public int Index { get; init; } = index;
    public int Length { get; init; } = length;

    public override string ToString()
    {
        return $"[{Type}, '{Value}']";
    }
}