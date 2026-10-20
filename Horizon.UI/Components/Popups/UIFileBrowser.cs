using System.Numerics;

using Horizon.Core.Platform;
using Horizon.UI;

namespace Horizon.UI.Components;

/// <summary>What a <see cref="UIFileBrowser"/> is for, a file that is there, a name to save to, or a folder.</summary>
public enum UIBrowse
{
    Open,
    Save,
    Folder
}

/// <summary>
/// Hex's own window for picking a file or a folder, the same on Windows, macOS and Linux (where there's no dialog of the
/// system's a program can count on: GTK's, KDE's or a portal's, whichever the desktop has). The folder it's in along
/// the top, which can be typed into (a path, ~ for home, Enter to go there or pick it), the places down the left (home,
/// the workspace's folders, the drives or what's mounted), what's in the folder on the right with the folders first,
/// and along the bottom the name (for a save), which files are shown, whether the hidden ones are, and the buttons.
/// A click picks, a second click (or Enter) opens a folder or takes a file. Saving over a file that's there asks first,
/// by saying so and wanting Save pressed again.
/// </summary>
public sealed class UIFileBrowser
{
    private const float WIDTH = 860.0f, HEIGHT = 560.0f, PLACES = 200.0f, GAP = 8.0f;
    private const int ROWS = 15;

    // What a folder's row says after its name, and the row that goes up
    private const string FOLDER_MARK = "/", UP = "..";

    private readonly UIBrowse kind;
    private readonly (string Name, string[] Patterns)[] filters;
    private readonly TaskCompletionSource<string?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TextBox path, name;
    private readonly ListBox places, entries;
    private readonly Dropdown filter;
    private readonly ToggleButton hidden;
    private readonly Label said;
    private readonly Button accept;

    // The places' paths, by their row, and the folder shown
    private readonly List<string> placePaths = [];
    private string folder;
    private string? confirming;

    // Whether the name along the bottom is a new folder's, being typed (New folder)
    private bool naming;
    private string? nameBefore;

    /// <summary>The window, for whoever shows it to put it where it goes.</summary>
    public WindowPanel Window { get; }

    /// <summary>What was picked, a full path, null when the window was closed without.</summary>
    public Task<string?> Picked => answer.Task;

    /// <param name="filters">What the files shown can be, "Pictures" and "*.png;*.jpg" say, all of them after these.</param>
    /// <param name="roots">Folders to have among the places, the workspace's.</param>
    public UIFileBrowser(UIBrowse kind, string title, string directory, string fileName, IReadOnlyList<(string Name, string Pattern)> filters, IEnumerable<string>? roots = null)
    {
        this.kind = kind;
        this.filters = [.. filters.Select(pair => (pair.Name, pair.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))), ("All files", ["*"])];
        folder = Directory.Exists(directory) ? Path.GetFullPath(directory) : PlatformPaths.Home;

        Window = new WindowPanel(title)
        {
            Icon = kind == UIBrowse.Folder ? "folder" : "file",
            Size = new Vector2(WIDTH, HEIGHT),
            MinSize = new Vector2(WIDTH, HEIGHT),
            ResizeGrip = false,
            Lift = 28.0f,
            OnClosed = () => Finish(null)
        };

        var body = new StackPanel { Spacing = GAP, Padding = new UIEdges(12.0f) };
        Window.Add(body);

        path = new TextBox { Size = new Vector2(WIDTH - 24.0f, 30.0f), MaxLength = 4096, OnSubmitted = Typed };
        body.Add(path);

        var middle = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP };
        body.Add(middle);
        places = new ListBox { Size = new Vector2(PLACES, 0.0f), Rows = ROWS, OnChanged = _ => GoToPlace() };
        entries = new ListBox { Size = new Vector2(WIDTH - 24.0f - PLACES - GAP, 0.0f), Rows = ROWS, OnChanged = Chosen, OnActivated = Activated };
        middle.Add(places);
        middle.Add(entries);

        name = new TextBox
        {
            Size = new Vector2(WIDTH - 24.0f, 30.0f),
            MaxLength = 1024,
            Text = fileName,
            Placeholder = kind == UIBrowse.Save ? "The file's name" : "A name, or a path",
            OnSubmitted = _ => Accept(),
            Visible = kind != UIBrowse.Folder
        };
        body.Add(name);

        var bottom = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP };
        body.Add(bottom);
        filter = new Dropdown([.. this.filters.Select(pair => pair.Name)])
        {
            Size = new Vector2(260.0f, 30.0f),
            OnChanged = _ => Show(folder),
            Visible = kind != UIBrowse.Folder
        };
        hidden = new ToggleButton { Label = "Hidden files", OnPressed = () => Show(folder) };
        said = new Label { Size = new Vector2(250.0f, 30.0f), Align = Horizon.Rendering.Origin.Left, Wrap = true };
        var make = new Button { Label = "New folder", Size = new Vector2(110.0f, 30.0f), OnPressed = NewFolder };
        var cancel = new Button { Label = "Cancel", Size = new Vector2(90.0f, 30.0f), OnPressed = () => Finish(null) };
        accept = new Button
        {
            Label = kind switch { UIBrowse.Save => "Save", UIBrowse.Folder => "Choose", _ => "Open" },
            Primary = true,
            Size = new Vector2(90.0f, 30.0f),
            OnPressed = Accept
        };
        bottom.Add(filter);
        bottom.Add(hidden);
        bottom.Add(said);
        bottom.Add(make);
        bottom.Add(cancel);
        bottom.Add(accept);

        MakePlaces(roots ?? []);
        Show(folder);
    }

    /// <summary>Helper method to list where a file usually is: home, its folders, the workspace's, and the drives.</summary>
    private void MakePlaces(IEnumerable<string> roots)
    {
        var names = new List<string>();
        void Add(string label, string at)
        {
            if (!Directory.Exists(at) || placePaths.Any(known => PlatformPaths.Same(known, at)))
                return;
            names.Add(label);
            placePaths.Add(Path.GetFullPath(at));
        }

        string home = PlatformPaths.Home;
        Add("Home", home);
        foreach (string root in roots)
            Add(Path.GetFileName(root.TrimEnd('/', '\\')) is { Length: > 0 } shortName ? shortName : root, root);
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Downloads", Path.Combine(home, "Downloads"));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

        // Drives on Windows, what's mounted elsewhere (USB sticks, other disks), and the top of it all
        if (OperatingSystem.IsWindows())
        {
            foreach (DriveInfo drive in SafeDrives())
                Add(drive.Name.TrimEnd('\\'), drive.Name);
        }
        else
        {
            foreach (string mounts in (string[])["/Volumes", "/media/" + Environment.UserName, "/run/media/" + Environment.UserName, "/mnt"])
            {
                foreach (string mounted in Folders(mounts))
                    Add(Path.GetFileName(mounted), mounted);
            }

            Add("/", "/");
        }

        places.Items = [.. names];
        places.Index = -1;
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(drive => drive.IsReady);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string[] Folders(string at)
    {
        try
        {
            return Directory.Exists(at) ? Directory.GetDirectories(at) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void GoToPlace()
    {
        if (places.Index >= 0 && places.Index < placePaths.Count)
            Show(placePaths[places.Index]);
    }

    /// <summary>Helper method to list a folder, folders first, then the files the filter lets through, by name.</summary>
    private void Show(string at)
    {
        string[] folders, files;
        try
        {
            var shown = new DirectoryInfo(at);
            bool all = hidden.State;
            folders = [.. shown.EnumerateDirectories().Where(item => all || !IsHidden(item)).Select(item => item.Name).Order(StringComparer.OrdinalIgnoreCase)];
            files = kind == UIBrowse.Folder ? [] : [.. shown.EnumerateFiles().Where(item => (all || !IsHidden(item)) && Matches(item.Name)).Select(item => item.Name).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e)
        {
            Say($"{at} can't be read, {e.Message}");
            return;
        }

        folder = Path.GetFullPath(at);
        path.Text = folder;
        confirming = null;
        Say(string.Empty);

        var items = new List<string>(folders.Length + files.Length + 1);
        if (Directory.GetParent(folder) is not null)
            items.Add(UP);
        items.AddRange(folders.Select(item => item + FOLDER_MARK));
        items.AddRange(files);
        entries.Items = [.. items];
        entries.Index = -1;

        // Where the places are, that one picked
        places.Index = placePaths.FindIndex(place => PlatformPaths.Same(place, folder));
    }

    private static bool IsHidden(FileSystemInfo item) => item.Name.StartsWith('.') || (item.Attributes & FileAttributes.Hidden) != 0;

    private bool Matches(string file)
    {
        string[] patterns = filters[Math.Clamp(Array.IndexOf(filters.Select(pair => pair.Name).ToArray(), filter.Value), 0, filters.Length - 1)].Patterns;
        foreach (string pattern in patterns)
        {
            if (pattern is "*" or "*.*")
                return true;
            if (pattern.StartsWith("*.", StringComparison.Ordinal) && file.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(file, pattern, PlatformPaths.Comparison))
                return true;
        }

        return false;
    }

    // What a row of the list is, a folder (and which) or a file
    private string? FolderOf(string item) => item == UP ? Directory.GetParent(folder)?.FullName
        : item.EndsWith(FOLDER_MARK, StringComparison.Ordinal) ? Path.Combine(folder, item[..^FOLDER_MARK.Length]) : null;

    private void Chosen(string item)
    {
        confirming = null;
        Say(string.Empty);
        if (FolderOf(item) is null && kind != UIBrowse.Folder)
            name.Text = item;
    }

    private void Activated(string item)
    {
        if (FolderOf(item) is { } inside)
        {
            Show(inside);
            return;
        }

        // The file of the row, whatever was typed before
        if (kind != UIBrowse.Folder)
            name.Text = item;
        Accept();
    }

    /// <summary>A path typed along the top: a folder is gone to, a file is taken, ~ is home.</summary>
    private void Typed(string typed)
    {
        string at = typed.Trim().Trim('"');
        if (at == "~" || at.StartsWith("~/", StringComparison.Ordinal))
            at = Path.Combine(PlatformPaths.Home, at.Length > 2 ? at[2..] : string.Empty);
        if (!Path.IsPathRooted(at))
            at = Path.Combine(folder, at);

        if (Directory.Exists(at))
        {
            Show(at);
            return;
        }

        bool takes = kind switch
        {
            UIBrowse.Open => File.Exists(at),
            UIBrowse.Save => Directory.Exists(Path.GetDirectoryName(at)),
            _ => false
        };

        if (!takes)
        {
            Say($"there's nothing at {at}");
            path.Text = folder;
            return;
        }

        Show(Path.GetDirectoryName(Path.GetFullPath(at))!);
        name.Text = Path.GetFileName(at);
        Accept();
    }

    /// <summary>The button along the bottom (or Enter): the folder that's open or picked, or the file named.</summary>
    private void Accept()
    {
        if (naming)
        {
            MakeFolder();
            return;
        }

        if (kind == UIBrowse.Folder)
        {
            string chosen = entries.Index >= 0 && FolderOf(entries.Value) is { } picked && entries.Value != UP ? picked : folder;
            Finish(chosen);
            return;
        }

        string typed = name.Text.Trim();
        if (typed.Length == 0)
        {
            Say(kind == UIBrowse.Save ? "it needs a name" : "pick a file");
            return;
        }

        string full = Path.GetFullPath(Path.IsPathRooted(typed) ? typed : Path.Combine(folder, typed));
        if (Directory.Exists(full))
        {
            Show(full);
            name.Text = string.Empty;
            return;
        }

        if (kind == UIBrowse.Open)
        {
            if (!File.Exists(full))
            {
                Say($"there's no {Path.GetFileName(full)} here");
                return;
            }

            Finish(full);
            return;
        }

        // Saving: the extension the filter is for when the name has none, and over a file that's there only when asked twice
        string[] patterns = filters[Math.Max(0, Array.IndexOf(filters.Select(pair => pair.Name).ToArray(), filter.Value))].Patterns;
        if (!Path.HasExtension(full) && patterns.FirstOrDefault(pattern => pattern.StartsWith("*.", StringComparison.Ordinal) && pattern != "*.*") is { } first)
            full += first[1..];

        if (!Directory.Exists(Path.GetDirectoryName(full)))
        {
            Say($"there's no folder {Path.GetDirectoryName(full)}");
            return;
        }

        if (File.Exists(full) && !PlatformPaths.Same(confirming ?? string.Empty, full))
        {
            confirming = full;
            Say($"{Path.GetFileName(full)} is there already, Save again to replace it");
            return;
        }

        Finish(full);
    }

    /// <summary>New folder: the name along the bottom is the new folder's until Enter (or the button) makes it.</summary>
    private void NewFolder()
    {
        if (naming)
        {
            MakeFolder();
            return;
        }

        naming = true;
        nameBefore = name.Text;
        name.Visible = true;
        name.Text = string.Empty;
        name.Placeholder = "The new folder's name, Enter makes it";
        name.Focus();
        Say("name the folder");
    }

    private void MakeFolder()
    {
        string typed = name.Text.Trim();
        if (typed.Length == 0 || typed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || typed is "." or "..")
        {
            Say("that can't be a folder's name");
            return;
        }

        string made = Path.Combine(folder, typed);
        try
        {
            if (Directory.Exists(made) || File.Exists(made))
            {
                Say($"there's a {typed} here already");
                return;
            }

            Directory.CreateDirectory(made);
        }
        catch (Exception e)
        {
            Say($"no folder could be made here, {e.Message}");
            return;
        }

        naming = false;
        name.Text = nameBefore ?? string.Empty;
        name.Placeholder = kind == UIBrowse.Save ? "The file's name" : "A name, or a path";
        name.Visible = kind != UIBrowse.Folder;
        Show(made);
    }

    /* What a test drives it by, the way the UI does */

    /// <summary>The folder it shows.</summary>
    public string Folder => folder;

    /// <summary>The rows of the list, ".." first, then the folders (with a / after them), then the files.</summary>
    public IReadOnlyList<string> Entries => entries.Items;

    /// <summary>What it last said along the bottom (why it didn't take something), empty for nothing.</summary>
    public string Message => said.Text;

    /// <summary>The name along the bottom.</summary>
    public string Name
    {
        get => name.Text;
        set => name.Text = value;
    }

    /// <summary>Whether the hidden files are shown.</summary>
    public bool ShowHidden
    {
        get => hidden.State;
        set
        {
            hidden.State = value;
            Show(folder);
        }
    }

    /// <summary>A row clicked once.</summary>
    public void Choose(string item)
    {
        entries.Value = item;
        Chosen(item);
    }

    /// <summary>A row clicked twice (or Enter on it).</summary>
    public void Activate(string item)
    {
        entries.Value = item;
        Activated(item);
    }

    /// <summary>A path typed along the top and Enter.</summary>
    public void Type(string typed) => Typed(typed);

    /// <summary>Open, Save or Choose pressed.</summary>
    public void Press() => Accept();

    /// <summary>New folder pressed, the name typed and Enter.</summary>
    public void MakeNewFolder(string folderName)
    {
        NewFolder();
        name.Text = folderName;
        MakeFolder();
    }

    /// <summary>Cancel pressed.</summary>
    public void Cancel() => Finish(null);

    private void Say(string text) => said.Text = text;

    private void Finish(string? picked)
    {
        if (!answer.TrySetResult(picked))
            return;

        Window.OnClosed = null;
        Window.Parent?.Remove(Window);
    }
}
