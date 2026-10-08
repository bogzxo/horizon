namespace Horizon.Core;

public static class Util
{
    /// <summary>
    /// Splits a string into its lines, whichever way the platform it came from ends them.
    /// </summary>
    public static IEnumerable<string> SplitToLines(this string input)
    {
        if (input is null)
            yield break;

        using var reader = new StringReader(input);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
