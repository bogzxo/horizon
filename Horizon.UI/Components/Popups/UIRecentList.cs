namespace Horizon.UI.Components;

/// <summary>
/// The last few things picked out of a chooser, the newest first, the commands of a palette or the files of quick open,
/// kept in a file of a line each when it's given one (and only in memory when it isn't, which is what a self test wants,
/// it'd have the palette of whoever ran it otherwise). A chooser puts them over the rest as a group of thinner rows.
/// </summary>
public sealed class UIRecentList
{
    private readonly List<string> items = [];
    private readonly string? file;

    /// <summary>How many it keeps.</summary>
    public int Most { get; }

    /// <summary>What was picked, the newest first.</summary>
    public IReadOnlyList<string> Items => items;

    public UIRecentList(string? file, int most = 5)
    {
        this.file = file;
        Most = most;

        if (file is null || !File.Exists(file))
            return;

        try
        {
            foreach (string line in File.ReadAllLines(file))
                if (line.Length > 0 && items.Count < most && !items.Contains(line))
                    items.Add(line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Puts what was just picked at the top, out of wherever it was, and writes the file.</summary>
    public void Remember(string text)
    {
        if (string.IsNullOrEmpty(text) || (items.Count > 0 && items[0] == text))
            return;

        items.Remove(text);
        items.Insert(0, text);
        if (items.Count > Most)
            items.RemoveRange(Most, items.Count - Most);

        if (file is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, items);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Forgets the lot, the file too.</summary>
    public void Clear()
    {
        items.Clear();
        if (file is not null)
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
