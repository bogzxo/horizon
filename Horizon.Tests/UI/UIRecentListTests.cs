using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>What a chooser remembers was picked last, the newest first, a few of them, kept in a file when it has one.</summary>
public sealed class UIRecentListTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "horizon-recent-" + Guid.NewGuid().ToString("N"), "recent.txt");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void The_newest_goes_first_once_and_the_oldest_falls_off()
    {
        var recent = new UIRecentList(null, most: 3);
        foreach (string picked in new[] { "a", "b", "c", "a", "d" })
            recent.Remember(picked);

        Assert.Equal(["d", "a", "c"], recent.Items);
    }

    [Fact]
    public void A_file_keeps_them_for_next_time_and_clearing_takes_it_away()
    {
        var recent = new UIRecentList(file);
        recent.Remember("open");
        recent.Remember("save");

        Assert.Equal(["save", "open"], new UIRecentList(file).Items);

        recent.Clear();
        Assert.Empty(recent.Items);
        Assert.False(File.Exists(file));
    }
}
