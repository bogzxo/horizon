namespace Horizon.UI.Components;

/// <summary>
/// Another layout file put in here, so a big UI is written as parts in files of their own. The parts of the file are
/// the including layout's as well, <see cref="UILayout.Get{T}"/> finds them by name, so the code that drives the UI
/// doesn't know or care which file a thing was written in. A group underneath, it draws nothing and is as big as
/// what the file put in it.
/// <code>
/// let activity = compositor.include({ parent: body, file: "shell/activity_bar.hor" });
/// </code>
/// The file is looked for next to the layout that includes it. What the included file says about the screen it was
/// made for and which layers start hidden is its own business and ignored here, the including layout says that.
/// </summary>
public class Include : Group
{
    /// <summary>The layout file, from the folder of the layout this is in.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>What was built out of the file, null until it has been.</summary>
    public UILayout? Layout { get; private set; }

    /// <summary>
    /// Builds the file in here, in place of whatever was in here before, and hands it back. The including layout
    /// takes the parts off it for its own.
    /// </summary>
    /// <param name="module">The module this is in.</param>
    /// <param name="from">The layout the file is named from, null to take <see cref="File"/> as it is.</param>
    public UILayout Reload(UIModule module, UILayout? from)
    {
        for (int i = Children.Count - 1; i >= 0; i--)
            Remove(Children[i]);

        string path = from?.Resolve(File) ?? File;
        Layout = UILayout.Load(module, path, this);
        return Layout;
    }

    protected override void DefineScript()
    {
        base.DefineScript();
        Expose("file", () => File, value => File = value);
    }
}
