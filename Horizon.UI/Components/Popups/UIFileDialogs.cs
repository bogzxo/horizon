namespace Horizon.UI.Components;

/// <summary>
/// Where a program shows a dialog and what it has for its places: <see cref="Present"/> puts the window over the program
/// (coloured as its other windows are), <see cref="Roots"/> are folders it wants at the top of the places, a workspace's.
/// </summary>
public sealed record UIDialogHost(Action<WindowPanel> Present, IReadOnlyList<string> Roots);

/// <summary>
/// A file to open, a place to save one, or a folder, picked in <see cref="UIFileBrowser"/>, the same on Windows, macOS
/// and Linux. Each hands back what was picked as a task, finished on the UI's thread when the window is done with, null
/// if it was closed without picking anything.
/// </summary>
public static class UIFileDialogs
{
    /// <summary>
    /// A file that is there. The filter is pairs of what it says and what it matches, "Pictures" and "*.png;*.jpg", all
    /// files before them. A file named in the folder is picked as it opens, the one somebody was about to open before a
    /// question took them out of the dialog.
    /// </summary>
    public static Task<string?> Open(string title, string directory, IReadOnlyList<(string Name, string Pattern)> filter, UIDialogHost host, string name = "") =>
        Show(host, UIBrowse.Open, title, directory, name, filter);

    /// <summary>Where to save, <paramref name="name"/> the name the file is given unless another one is typed.</summary>
    public static Task<string?> Save(string title, string directory, string name, IReadOnlyList<(string Name, string Pattern)> filter, UIDialogHost host) =>
        Show(host, UIBrowse.Save, title, directory, name, filter);

    /// <summary>A folder, starting in the folder given.</summary>
    public static Task<string?> Folder(string title, string directory, UIDialogHost host) =>
        Show(host, UIBrowse.Folder, title, directory, string.Empty, []);

    private static Task<string?> Show(UIDialogHost host, UIBrowse kind, string title, string directory, string name, IReadOnlyList<(string, string)> filter)
    {
        var browser = new UIFileBrowser(kind, title, directory, name, filter, host.Roots);
        host.Present(browser.Window);
        browser.TakeKeyboard();
        return browser.Picked;
    }
}
