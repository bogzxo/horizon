using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests;

/// <summary>
/// The file dialog off screen, driven the way the keys and the buttons drive it, over a folder of its own in the temp
/// folder. What a row says along the right, a few letters picking a row, back and forward, and an empty folder saying so.
/// </summary>
public sealed class UIFileBrowserTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "horizon_browser_" + Guid.NewGuid().ToString("N"));

    public UIFileBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "app.d"), "void main() {}");
        File.WriteAllText(Path.Combine(root, "notes.txt"), new string('x', 1500));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private UIFileBrowser Open() => new(UIBrowse.Open, "Open", root, string.Empty, []);

    [Fact]
    public void Rows_have_their_folders_first_and_say_a_files_size_and_when_it_changed()
    {
        UIFileBrowser browser = Open();

        Assert.Equal(["..", "empty/", "sub/", "app.d", "notes.txt"], browser.Entries);
        Assert.Equal("2 KB", browser.DetailsOf("notes.txt")[0]);
        Assert.StartsWith("Today", browser.DetailsOf("notes.txt")[1]);
        Assert.Equal(string.Empty, browser.DetailsOf("sub/")[0]);
        Assert.Equal(string.Empty, browser.Note);
    }

    [Fact]
    public void A_folder_with_nothing_in_it_says_so()
    {
        UIFileBrowser browser = Open();
        browser.Activate("empty/");

        Assert.Equal([".."], browser.Entries);
        Assert.Equal("This folder is empty", browser.Note);
    }

    [Fact]
    public void A_few_letters_of_a_name_pick_its_row_and_Enter_takes_it()
    {
        UIFileBrowser browser = Open();
        browser.Key('n');
        browser.Key('o');

        Assert.Equal("notes.txt", browser.Selected);
        Assert.Equal("no", browser.Name);

        browser.Press();
        Assert.True(browser.Picked.IsCompleted);
        Assert.Equal(Path.Combine(root, "notes.txt"), browser.Picked.Result);
    }

    [Fact]
    public void A_few_letters_of_a_folder_go_into_it()
    {
        UIFileBrowser browser = Open();
        browser.Key('s');
        browser.Press();

        Assert.Equal(Path.Combine(root, "sub"), browser.Folder);
        Assert.Equal(string.Empty, browser.Name);
        Assert.False(browser.Picked.IsCompleted);
    }

    [Fact]
    public void Back_goes_to_the_folder_before_with_the_one_left_picked_and_forward_goes_in_again()
    {
        UIFileBrowser browser = Open();
        browser.Activate("sub/");
        Assert.Equal(Path.Combine(root, "sub"), browser.Folder);

        browser.GoBack();
        Assert.Equal(root, browser.Folder);
        Assert.Equal("sub/", browser.Selected);

        browser.GoForward();
        Assert.Equal(Path.Combine(root, "sub"), browser.Folder);
    }

    [Fact]
    public void Going_up_by_its_row_picks_the_folder_come_out_of()
    {
        UIFileBrowser browser = Open();
        browser.Activate("sub/");
        browser.Activate("..");

        Assert.Equal(root, browser.Folder);
        Assert.Equal("sub/", browser.Selected);
    }

    [Fact]
    public void A_folders_row_takes_away_the_name_a_files_row_gave_but_not_one_typed()
    {
        UIFileBrowser browser = Open();
        browser.Choose("app.d");
        Assert.Equal("app.d", browser.Name);
        browser.Choose("sub/");
        Assert.Equal(string.Empty, browser.Name);

        browser.Key('x');
        browser.Choose("empty/");
        Assert.Equal("x", browser.Name);
    }

    [Fact]
    public void The_path_is_crumbs_of_its_folders_and_a_crumb_goes_there()
    {
        UIFileBrowser browser = Open();
        browser.Activate("sub/");

        Assert.Equal("sub", browser.Crumbs[^1]);
        Assert.Equal(Path.GetFileName(root), browser.Crumbs[^2]);
        Assert.StartsWith("$(server)", browser.Crumbs[0]);

        browser.PressCrumb(browser.Crumbs.Count - 2);
        Assert.Equal(root, browser.Folder);
        Assert.Equal(Path.GetFileName(root), browser.Crumbs[^1]);
    }

    [Fact]
    public void A_dialog_that_carries_on_from_the_one_before_can_go_back_where_that_one_had_been()
    {
        UIFileBrowser first = Open();
        first.Activate("sub/");
        first.Cancel();

        var again = new UIFileBrowser(UIBrowse.Open, "Open", Path.Combine(root, "sub"), "nothing.txt", [], resume: true);
        again.GoBack();
        Assert.Equal(root, again.Folder);
    }

    [Fact]
    public void A_file_kind_has_its_symbol_and_whether_it_reads_as_text()
    {
        Assert.Equal("code", UIFileKinds.IconOf("app.d"));
        Assert.Equal("file-media", UIFileKinds.IconOf("cat.PNG"));
        Assert.Equal("file", UIFileKinds.IconOf("README"));
        Assert.True(UIFileKinds.IsText("app.d"));
        Assert.True(UIFileKinds.IsText("README"));
        Assert.False(UIFileKinds.IsText("cat.png"));
        Assert.False(UIFileKinds.IsText("Hex.dll"));
        Assert.Equal("999 bytes", UIFileKinds.SizeOf(999));
        Assert.Equal("1.5 MB", UIFileKinds.SizeOf(1_500_000));
        Assert.Equal("12 Oct 2026", UIFileKinds.WhenOf(new DateTime(2026, 10, 12, 9, 0, 0), new DateTime(2026, 10, 22, 9, 0, 0)));
        Assert.Equal("Yesterday, 09:05", UIFileKinds.WhenOf(new DateTime(2026, 10, 21, 9, 5, 0), new DateTime(2026, 10, 22, 9, 0, 0)));
    }
}
