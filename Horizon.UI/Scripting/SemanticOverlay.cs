namespace Horizon.UI.Scripting;

/// <summary>
/// Lays what something that knows the code says the names are (semantic spans) over what a grammar coloured a line in,
/// the way VS Code lays semantic tokens over TextMate. A name written over keeps nothing of the grammar's span under it
/// but the pieces either side. What the grammar called a text, a comment, a number or an escape stays the grammar's
/// whatever is said about it, a server that classifies the inside of a string doesn't get to recolour it.
/// </summary>
public static class SemanticOverlay
{
    /// <summary>
    /// The spans of a line with the names laid over them, into a list that's cleared first. Both have to be in order
    /// along the line, and a name that runs into the one before it is dropped.
    /// </summary>
    public static void Overlay(List<SyntaxSpan> under, List<SyntaxSpan> over, List<SyntaxSpan> into)
    {
        into.Clear();
        int g = 0, at = 0;
        for (int o = 0; o < over.Count; o++)
        {
            SyntaxSpan name = over[o];
            if (name.Length <= 0 || name.Start < at || Kept(under, g, name))
                continue;

            // What the grammar had before the name, all of it up to where the name starts
            while (g < under.Count && under[g].Start + under[g].Length <= name.Start)
                Clip(under[g++], at, int.MaxValue, into);
            if (g < under.Count && under[g].Start < name.Start)
                Clip(under[g], at, name.Start, into);

            into.Add(name);
            at = name.Start + name.Length;
            while (g < under.Count && under[g].Start + under[g].Length <= at)
                g++;
        }

        for (; g < under.Count; g++)
            Clip(under[g], at, int.MaxValue, into);
    }

    /// <summary>Whether a kind the grammar said is one a semantic span never writes over.</summary>
    public static bool Keeps(SyntaxKind kind) =>
        kind is SyntaxKind.Text or SyntaxKind.Comment or SyntaxKind.Number or SyntaxKind.Escape or SyntaxKind.Markup or SyntaxKind.Reference;

    // Whether any of the grammar's spans under a name is one it doesn't get to write over
    private static bool Kept(List<SyntaxSpan> under, int from, SyntaxSpan name)
    {
        int end = name.Start + name.Length;
        for (int i = from; i < under.Count && under[i].Start < end; i++)
        {
            if (under[i].Start + under[i].Length > name.Start && Keeps(under[i].Kind))
                return true;
        }

        return false;
    }

    // The piece of a span between two places, when there's any of it
    private static void Clip(SyntaxSpan span, int from, int to, List<SyntaxSpan> into)
    {
        int start = Math.Max(span.Start, from), end = Math.Min(span.Start + span.Length, to);
        if (end > start)
            into.Add(span with { Start = start, Length = end - start });
    }
}
