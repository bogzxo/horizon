namespace Horizon.HIDL.Parsing;

public class ParseException : Exception
{
    public int Line { get; }
    public int Col { get; }
    public int Length { get; }

    public ParseException(string message, int line, int col, int length = 1) : base(message)
    {
        Line = line;
        Col = col;
        Length = length;
    }
}
