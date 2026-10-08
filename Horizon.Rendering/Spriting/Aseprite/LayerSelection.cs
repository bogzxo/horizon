namespace Horizon.Rendering.Spriting;

/// <summary>
/// Which layers of a layered image (an Aseprite file) are drawn. Nothing chosen draws the layers the way the file was
/// saved, which is what every image that has no layers gets as well.
/// A file that keeps the same art in several colours has a layer (or a group of them) for every colour, and whichever
/// one was showing when it was saved is no use to anybody: <see cref="Only"/> shows one of them whatever the file says
/// and hides the others. Layers nobody names (a background they all share) stay as the file has them.
/// </summary>
/// <param name="Spec">
/// The layers that are shown and hidden whatever the file says, written "+White,-Black,-Gold". A layer goes by its
/// name or by its path through the groups it is in ("White/Main"), and a group takes everything inside it along.
/// </param>
public readonly record struct LayerSelection(string? Spec)
{
    private const char SHOW = '+';
    private const char HIDE = '-';
    private const char SEPARATOR = ',';

    /// <summary>Whether the layers are left the way the file has them.</summary>
    public bool IsDefault => string.IsNullOrEmpty(Spec);

    /// <summary>
    /// Shows one layer out of a set of them and hides the rest of the set.
    /// </summary>
    /// <param name="show">The layer (or group) to show.</param>
    /// <param name="among">Every layer of the set, the one that is shown may be in here or not.</param>
    public static LayerSelection Only(string show, IEnumerable<string> among)
    {
        var parts = new List<string> { SHOW + show };
        foreach (string other in among)
        {
            if (!string.Equals(other, show, StringComparison.OrdinalIgnoreCase))
                parts.Add(HIDE + other);
        }

        return new LayerSelection(string.Join(SEPARATOR, parts));
    }

    /// <summary>
    /// Shows these layers whatever the file says, and leaves the others alone.
    /// </summary>
    public static LayerSelection Showing(params string[] layers) =>
        new(string.Join(SEPARATOR, layers.Select(layer => SHOW + layer)));

    /// <summary>
    /// Hides these layers whatever the file says, and leaves the others alone.
    /// </summary>
    public static LayerSelection Hiding(params string[] layers) =>
        new(string.Join(SEPARATOR, layers.Select(layer => HIDE + layer)));

    /// <summary>
    /// What the selection wants of a layer. True for shown, false for hidden and null for whatever the file says.
    /// </summary>
    /// <param name="name">The name of the layer.</param>
    /// <param name="path">Its path through the groups it is in, which is its name for a layer that is in none.</param>
    public bool? Wants(string name, string path)
    {
        if (Spec is not { Length: > 0 } spec) return null;

        foreach (var part in spec.AsSpan().Split(SEPARATOR))
        {
            ReadOnlySpan<char> entry = spec.AsSpan(part).Trim();
            if (entry.Length < 2) continue;

            ReadOnlySpan<char> layer = entry[1..];
            if (!layer.Equals(name, StringComparison.OrdinalIgnoreCase) && !layer.Equals(path, StringComparison.OrdinalIgnoreCase))
                continue;

            return entry[0] != HIDE;
        }

        return null;
    }

    public override string ToString() => Spec ?? string.Empty;
}
