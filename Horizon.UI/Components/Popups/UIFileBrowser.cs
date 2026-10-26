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
/// the top as crumbs, a click on one goes there and a click past them types a path (~ for home, Enter to go there or
/// pick it), the places down the left (home,
/// the workspace's folders, the drives or what's mounted), what's in the folder on the right with the folders first,
/// and along the bottom the name (for a save), which files are shown, whether the hidden ones are, and the buttons.
/// A click picks, a second click (or Enter) opens a folder or takes a file. Saving over a file that's there asks first,
/// by saying so and wanting Save pressed again. It has the keyboard while it's up (<see cref="TakeKeyboard"/>), up and
/// down go along the list from the box that has it, Enter opens what was gone to, Backspace with no name goes up a folder.
/// </summary>
public sealed partial class UIFileBrowser
{
    private const float WIDTH = 860.0f, HEIGHT = 600.0f, PLACES = 200.0f, GAP = 8.0f, HIDDEN = 140.0f, FILTER = 260.0f;
    private const int ROWS = 14;

    // How wide the list's size and changed columns are, and how big the buttons in front of the path
    private const float SIZE_WIDTH = 72.0f, CHANGED_WIDTH = 120.0f, NAV = 30.0f;

    // How far in from the path box's sides its crumbs are, their first piece's words where a typed path's start
    private const float CRUMBS_INSET = 8.0f;

    // What a folder's row says after its name, and the row that goes up
    private const string FOLDER_MARK = "/", UP = "..";

    /// <summary>What the filter that lets every file through is called, the one the browser starts on.</summary>
    public const string ALL_FILES = "All files";

    private readonly UIBrowse kind;
    private readonly (string Name, string[] Patterns)[] filters;
    private readonly TaskCompletionSource<string?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TextBox path, name;

    // The path as pieces over the box it's typed in, and the folder each piece is
    private readonly Breadcrumbs crumbs;
    private readonly List<string> crumbPaths = [];
    private readonly ListBox places, entries;
    private readonly Dropdown filter;
    private readonly ToggleButton hidden;
    private readonly Label said;
    private readonly Button accept;

    // The places' paths and symbols, by their row, and the folder shown
    private readonly List<string> placePaths = [];
    private readonly Dictionary<string, string> placeIcons = [];
    private string folder;

    // What the list says about each of its rows, a file's size and when it was changed, made once as the folder is shown
    private readonly Dictionary<string, string[]> details = [];

    // The folders gone back from and forward from, and their buttons with the one up. The last dialog's way back is kept
    // for one that carries on from it
    private readonly Stack<string> backward = [], onward = [];
    private static string[] previousBackward = [];
    private readonly Button backButton, forwardButton, upButton;
    private string? confirming;

    // Whether the name along the bottom is a new folder's, being typed (New folder)
    private bool naming;
    private string? nameBefore;

    // Whether the row picked was gone to with the keys since anything was typed, Enter opens it then, and what had the
    // keyboard before the browser took it
    private bool keyed;
    private UIComponent? focusBefore;

    /// <summary>The window, for whoever shows it to put it where it goes.</summary>
    public WindowPanel Window { get; }

    /// <summary>What was picked, a full path, null when the window was closed without.</summary>
    public Task<string?> Picked => answer.Task;

    /// <param name="filters">
    /// What the files shown can be, "Pictures" and "*.png;*.jpg" say. All of them comes first and is what it starts on, a
    /// file of a kind nobody thought of is still a file somebody may want, and a name saved under it keeps whatever
    /// extension was typed. One of these picked gives a name with none its first.
    /// </param>
    /// <param name="roots">Folders to have among the places, the workspace's.</param>
    /// <param name="resume">
    /// Whether it carries on from the dialog before, its folders gone back to with Back. For the one that comes up again
    /// after a question took somebody out of it (a file that isn't text), whose Back history was lost.
    /// </param>
    public UIFileBrowser(UIBrowse kind, string title, string directory, string fileName, IReadOnlyList<(string Name, string Pattern)> filters, IEnumerable<string>? roots = null, bool resume = false)
    {
        this.kind = kind;
        if (resume)
        {
            foreach (string before in Enumerable.Reverse(previousBackward))
                backward.Push(before);
        }
        this.filters = [(ALL_FILES, ["*"]), .. filters.Select(pair => (pair.Name, pair.Pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))];
        folder = Directory.Exists(directory) ? Path.GetFullPath(directory) : PlatformPaths.Home;
        Opened();

        Window = new WindowPanel(title)
        {
            Icon = kind == UIBrowse.Folder ? "folder" : "file",
            Size = new Vector2(WIDTH, HEIGHT),
            MinSize = new Vector2(WIDTH, HEIGHT),
            ResizeGrip = false,
            Lift = 28.0f,
            OnClosed = () => Finish(null)
        };

        // From the top, a folder's dialog has one row less and in the middle it had a band of nothing under the title
        var body = new StackPanel { Spacing = GAP, Padding = new UIEdges(12.0f), Anchor = Horizon.Rendering.Origin.Top, Pivot = Horizon.Rendering.Origin.Top };
        Window.Add(body);

        // Escape while a new folder's being named lets go of the name, not of the whole dialog
        Window.BeforeEscape = () =>
        {
            if (!naming)
                return false;

            StopNaming();
            return true;
        };

        // Back, forward and up a folder in front of the path, the way a Finder window has them
        var top = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP };
        body.Add(top);
        backButton = new Button { Icon = "arrow-left", Size = new Vector2(NAV, NAV), Tooltip = "Back", OnPressed = Back };
        forwardButton = new Button { Icon = "arrow-right", Size = new Vector2(NAV, NAV), Tooltip = "Forward", OnPressed = Forward };
        upButton = new Button { Icon = "arrow-up", Size = new Vector2(NAV, NAV), Tooltip = "Up a folder", OnPressed = GoUp };
        top.Add(backButton);
        top.Add(forwardButton);
        top.Add(upButton);

        // The path is its folders as crumbs, a click on one goes there, and a click past them (or Tab) types a path into the
        // box under them, Explorer's address bar. It was the box alone, a long path a wall of text cut off at the front
        path = new TextBox { Size = new Vector2(WIDTH - 24.0f - (NAV + GAP) * 3.0f, 30.0f), MaxLength = 4096, OnSubmitted = TypedPath, TextWhileTyped = true };
        path.TakeKey = key => Keyed(key, path);
        path.OnChanged = _ => keyed = false;
        var where = new Panel { Size = path.Size };
        top.Add(where);
        where.Add(path);
        crumbs = new Breadcrumbs
        {
            Editor = path,
            Size = new Vector2(path.Size.X - CRUMBS_INSET * 2.0f, path.Size.Y - 6.0f),
            Anchor = Horizon.Rendering.Origin.Left,
            Pivot = Horizon.Rendering.Origin.Left,
            Position = new Vector2(CRUMBS_INSET, 0.0f),
            OnPressed = index => GoTo(crumbPaths[index])
        };
        where.Add(crumbs);

        // The places a row taller than the list beside them, which has its headings over its rows, so the two end together
        var middle = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP };
        body.Add(middle);
        places = new ListBox { Size = new Vector2(PLACES, 0.0f), Rows = ROWS, OnChanged = _ => GoToPlace(), Icon = PlaceIcon, TakesKeys = true };
        entries = new ListBox
        {
            Size = new Vector2(WIDTH - 24.0f - PLACES - GAP, 0.0f),
            Rows = ROWS - 1,
            OnChanged = Chosen,
            OnActivated = Activated,
            Icon = EntryIcon,
            Columns = item => (string.Empty, Shown(item), string.Empty),
            Details = item => details.TryGetValue(item, out string[]? said) ? said : [],
            DetailWidths = [SIZE_WIDTH, CHANGED_WIDTH],
            Headings = ["Name", "Size", "Modified"]
        };
        middle.Add(places);
        middle.Add(entries);

        name = new TextBox
        {
            Size = new Vector2(WIDTH - 24.0f, 30.0f),
            MaxLength = 1024,
            Text = fileName,
            Placeholder = NamePlaceholder,
            OnSubmitted = _ => Accept()
        };
        name.TakeKey = key => Keyed(key, name);
        name.OnChanged = Seek;
        body.Add(name);

        // Which files are shown and whether the hidden ones are, with what it says beside them on the rest of the row, and
        // under that New folder on the left and the buttons on the right, the one Enter presses last. They were one row,
        // wider than the window, and what it said ran into the toggle's words
        var options = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP, Anchor = Horizon.Rendering.Origin.Left, Pivot = Horizon.Rendering.Origin.Left };
        body.Add(options);
        filter = new Dropdown([.. this.filters.Select(pair => pair.Name)])
        {
            Size = new Vector2(FILTER, 30.0f),
            OnChanged = _ => Show(folder),
            Visible = kind != UIBrowse.Folder
        };
        hidden = new ToggleButton { Label = "Hidden files", Size = new Vector2(HIDDEN, 30.0f), OnPressed = () => Show(folder) };
        said = new Label { Size = new Vector2(WIDTH - 24.0f - FILTER - HIDDEN - GAP * 2.0f, 30.0f), Align = Horizon.Rendering.Origin.Left, Wrap = true };
        var make = new Button { Label = "New folder", Size = new Vector2(110.0f, 30.0f), OnPressed = NewFolder };
        var cancel = new Button { Label = "Cancel", Size = new Vector2(90.0f, 30.0f), OnPressed = () => Finish(null) };
        accept = new Button
        {
            Label = AcceptLabel,
            Primary = true,
            Size = new Vector2(90.0f, 30.0f),
            OnPressed = Accept
        };
        // A folder's dialog has no filter, the toggle starts the row then rather than floating where the filter would be
        if (kind != UIBrowse.Folder)
            options.Add(filter);
        options.Add(hidden);
        options.Add(said);
        if (kind == UIBrowse.Folder)
            said.Size = said.Size with { X = said.Size.X + FILTER + GAP };

        var actions = new Panel { Size = new Vector2(WIDTH - 24.0f, 30.0f) };
        body.Add(actions);
        actions.Add(new SideButtons(this));
        make.Anchor = Horizon.Rendering.Origin.Left;
        make.Pivot = Horizon.Rendering.Origin.Left;
        actions.Add(make);
        var buttons = actions.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP, Anchor = Horizon.Rendering.Origin.Right, Pivot = Horizon.Rendering.Origin.Right });
        buttons.Add(cancel);
        buttons.Add(accept);

        MakePlaces(roots ?? []);
        Show(folder);
    }

    /// <summary>Helper method to list where a file usually is: home, its folders, the workspace's, and the drives.</summary>
    private void MakePlaces(IEnumerable<string> roots)
    {
        var names = new List<string>();
        void Add(string label, string at, string icon)
        {
            if (!Directory.Exists(at) || placePaths.Any(known => PlatformPaths.Same(known, at)))
                return;
            names.Add(label);
            placePaths.Add(Path.GetFullPath(at));
            placeIcons.TryAdd(label, icon);
        }

        string home = PlatformPaths.Home;
        Add("Home", home, "home");
        foreach (string root in roots)
            Add(Path.GetFileName(root.TrimEnd('/', '\\')) is { Length: > 0 } shortName ? shortName : root, root, "root-folder");
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "device-desktop");
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "files");
        Add("Downloads", Path.Combine(home, "Downloads"), "desktop-download");
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "file-media");

        // Drives on Windows, what's mounted elsewhere (USB sticks, other disks), and the top of it all
        if (OperatingSystem.IsWindows())
        {
            foreach (DriveInfo drive in SafeDrives())
                Add(drive.Name.TrimEnd('\\'), drive.Name, "server");
        }
        else
        {
            foreach (string mounts in (string[])["/Volumes", "/media/" + Environment.UserName, "/run/media/" + Environment.UserName, "/mnt"])
            {
                foreach (string mounted in Folders(mounts))
                    Add(Path.GetFileName(mounted), mounted, "server");
            }

            Add("/", "/", "server");
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
            GoTo(placePaths[places.Index]);
    }

    /// <summary>Helper method to list a folder, folders first, then the files the filter lets through, by name.</summary>
    private void Show(string at)
    {
        DirectoryInfo[] folders;
        FileInfo[] files;
        bool filtered;
        try
        {
            var shown = new DirectoryInfo(at);
            bool all = hidden.State;
            folders = [.. shown.EnumerateDirectories().Where(item => all || !IsHidden(item)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];
            FileInfo[] seen = kind == UIBrowse.Folder ? [] : [.. shown.EnumerateFiles().Where(item => all || !IsHidden(item))];
            files = [.. seen.Where(item => Matches(item.Name)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];
            filtered = files.Length < seen.Length;
        }
        catch (Exception e)
        {
            Say($"{at} can't be read, {e.Message}");
            return;
        }

        folder = Path.GetFullPath(at);
        path.Text = folder;
        SetCrumbs();
        confirming = null;
        Say(string.Empty);

        // What each row says along the right, once now rather than asking the disk every time it's painted
        details.Clear();
        DateTime now = DateTime.Now;
        var items = new List<string>(folders.Length + files.Length + 1);
        if (Directory.GetParent(folder) is not null)
            items.Add(UP);
        foreach (DirectoryInfo inside in folders)
        {
            items.Add(inside.Name + FOLDER_MARK);
            details[inside.Name + FOLDER_MARK] = [string.Empty, Changed(inside, now)];
        }
        foreach (FileInfo file in files)
        {
            items.Add(file.Name);
            details[file.Name] = [Size(file), Changed(file, now)];
        }

        entries.Items = [.. items];
        entries.Index = -1;

        // A folder with nothing in it says so, and one with nothing of the kind picked says that, the filter's hiding the rest
        entries.Note = folders.Length + files.Length > 0 ? string.Empty
            : filtered ? $"No {filter.Value} in here"
            : kind == UIBrowse.Folder ? "There are no folders in here" : "This folder is empty";

        // Where the places are, that one picked, and which ways there are to go from here
        places.Index = placePaths.FindIndex(place => PlatformPaths.Same(place, folder));
        backButton.Enabled = backward.Count > 0;
        forwardButton.Enabled = onward.Count > 0;
        upButton.Enabled = Directory.GetParent(folder) is not null;
    }

    // The folder shown as its pieces from the top down, the drive (or /) first with a symbol of one
    private void SetCrumbs()
    {
        var pieces = new List<string>();
        crumbPaths.Clear();
        for (DirectoryInfo? at = new(folder); at is not null; at = at.Parent)
        {
            string top = at.FullName.TrimEnd('/', '\\');
            pieces.Insert(0, at.Parent is null ? $"$(server) {(top.Length > 0 ? top : "/")}" : at.Name);
            crumbPaths.Insert(0, at.FullName);
        }

        crumbs.Set(pieces);
    }

    private static bool IsHidden(FileSystemInfo item) => item.Name.StartsWith('.') || (item.Attributes & FileAttributes.Hidden) != 0;

    // A file's size and when something was changed, nothing for what can't be asked (a file gone since it was listed)
    private static string Size(FileInfo file)
    {
        try
        {
            return UIFileKinds.SizeOf(file.Length);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Changed(FileSystemInfo item, DateTime now)
    {
        try
        {
            return UIFileKinds.WhenOf(item.LastWriteTime, now);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // What the button that takes it says
    private string AcceptLabel => kind switch { UIBrowse.Save => "Save", UIBrowse.Folder => "Choose", _ => "Open" };

    // What a row of the list says, a folder without the mark it's kept by (its symbol says it's a folder)
    private static string Shown(string item) => item != UP && item.EndsWith(FOLDER_MARK, StringComparison.Ordinal) ? item[..^FOLDER_MARK.Length] : item;

    // The symbol in front of a row, a folder blue, a file by its kind, the way up a quiet arrow
    private (string Name, Vector4? Color) EntryIcon(string item)
    {
        if (item == UP)
            return ("arrow-up", null);
        if (item.EndsWith(FOLDER_MARK, StringComparison.Ordinal))
            return ("folder", UIFileKinds.FOLDER);

        return (UIFileKinds.IconOf(item), Window.Module?.Compositor.Skin is { } skin ? UIFileKinds.ColourOf(item, skin.Syntax) : null);
    }

    private (string Name, Vector4? Color) PlaceIcon(string item) => (placeIcons.TryGetValue(item, out string? icon) ? icon : "folder", null);

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

    // Whether the name along the bottom is a row's that was picked rather than typed, which a folder's row takes away
    private bool nameFromRow;

    private void Chosen(string item)
    {
        confirming = null;
        Say(string.Empty);
        if (kind == UIBrowse.Folder)
            return;

        // A file's row gives the name, a folder's (or ..) takes away the one a file's gave. It stayed, and the box said a
        // file over a picked .. as if Enter would open it
        if (FolderOf(item) is null)
        {
            name.Text = item;
            nameFromRow = true;

            // Picked out, so a letter typed next is a new start that goes to the first row it begins, the way a click
            // and a key go in Explorer and the Finder. It was put after the name and found nothing
            name.SelectAll();
        }
        else if (nameFromRow)
        {
            name.Text = string.Empty;
            nameFromRow = false;
        }
    }

    private void Activated(string item)
    {
        // Up, with the folder come out of picked, the way Backspace goes
        if (item == UP)
        {
            GoUp();
            return;
        }

        if (FolderOf(item) is { } inside)
        {
            GoTo(inside);
            return;
        }

        // The file of the row, whatever was typed before
        if (kind != UIBrowse.Folder)
            name.Text = item;
        Accept();
    }

    /// <summary>
    /// The browser takes the keyboard once its window is up, the name along the bottom (a folder's dialog has it too, for a
    /// folder in the one shown or a path), and whoever had it gets it back when the window goes. It never took it, and every key
    /// went on to the code or the terminal behind it.
    /// </summary>
    public void TakeKeyboard()
    {
        TextBox box = name;
        if (box.Module is not { } module)
            return;

        focusBefore = module.Compositor.Focus;
        box.Focus();

        // A name to save as has its name picked and not its extension, the way a Mac's save dialog does, what's typed
        // over it keeps the kind of file it was
        if (kind == UIBrowse.Save && Path.GetExtension(box.Text) is { Length: > 0 } extension && extension.Length < box.Text.Length)
            box.Select(0, box.Text.Length - extension.Length);
        else
            box.SelectAll();

        // A file to open that was named is the row picked, the way back into a dialog that was left for a moment
        if (kind == UIBrowse.Open && box.Text.Length > 0 && Array.IndexOf(entries.Items, box.Text) is >= 0 and var at)
        {
            entries.Choose(at);
            keyed = true;
        }
    }

    // The list's keys from either box. Up and down along it, Enter on the row gone to, Backspace with no name goes up
    private bool Keyed(char key, TextBox from)
    {
        switch (key)
        {
            case UIKeyboard.UP: Step(-1); return true;
            case UIKeyboard.DOWN: Step(1); return true;
            case UIKeyboard.PAGE_UP: Step(1 - ROWS); return true;
            case UIKeyboard.PAGE_DOWN: Step(ROWS - 1); return true;

            case UIKeyboard.ENTER when keyed && entries.Index >= 0:
                Activated(entries.Value);
                return true;

            case UIKeyboard.BACKSPACE when from == name && name.Text.Length == 0 && !naming:
                GoUp();
                return true;

            // Alt and up, up a folder whatever's typed, the way a Windows Explorer and a Finder go up
            case UIKeyboard.MOVE_LINES_UP when !naming:
                GoUp();
                return true;

            default:
                return false;
        }
    }

    // Along the list by so many rows, from the top or the bottom when nothing's picked
    private void Step(int by)
    {
        int count = entries.Items.Length;
        if (count == 0)
            return;

        entries.Choose(entries.Index < 0 ? (by > 0 ? 0 : count - 1) : Math.Clamp(entries.Index + by, 0, count - 1));
        keyed = true;
    }

    // Up a folder with the one it came out of picked, the way back in
    private void GoUp()
    {
        if (Directory.GetParent(folder) is not { } parent)
            return;

        string came = folder;
        GoTo(parent.FullName);
        PickFolder(came);
    }

    /// <summary>Helper method to go to another folder, the one left is the way back and there's no way forward any more.</summary>
    private void GoTo(string at)
    {
        string left = folder;
        Show(at);
        if (PlatformPaths.Same(left, folder))
            return;

        backward.Push(left);
        onward.Clear();
        backButton.Enabled = true;
        forwardButton.Enabled = false;
    }

    // Back to the folder before, and forward again, with the one come out of picked
    private void Back() => Travel(backward, onward);

    private void Forward() => Travel(onward, backward);

    private void Travel(Stack<string> from, Stack<string> to)
    {
        if (from.Count == 0)
            return;

        string left = folder;
        to.Push(left);
        Show(from.Pop());
        PickFolder(left);
    }

    // Helper method to pick the row of a folder inside the one shown, the one just come out of
    private void PickFolder(string inside)
    {
        string row = Path.GetFileName(inside.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + FOLDER_MARK;
        if (Array.IndexOf(entries.Items, row) is >= 0 and var at)
        {
            entries.Choose(at);
            keyed = true;
        }
    }

    /// <summary>
    /// What's typed as a name picks the first row it's the start of (a few letters of a file, the way a Finder window goes
    /// to one as it's typed), without the name being written over, and Enter then takes that row if there's no file of
    /// the name itself. Not for a save, where what's typed is the new file's name.
    /// </summary>
    private void Seek(string typed)
    {
        keyed = false;
        nameFromRow = false;
        if (kind == UIBrowse.Save || naming)
            return;

        string start = typed.Trim();
        if (start.Length == 0 || start.IndexOfAny(['/', '\\']) >= 0)
        {
            entries.Index = -1;
            return;
        }

        entries.Index = Array.FindIndex(entries.Items, item => item != UP && Shown(item).StartsWith(start, StringComparison.OrdinalIgnoreCase));
    }

    // The row a typed name is the start of, the one Seek picked, null when the name was typed over since
    private string? Sought(string typed) => entries.Index >= 0 && entries.Value != UP && typed.Length > 0
        && Shown(entries.Value).StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? entries.Value : null;

    // Enter along the top. The folder that's open as it is picks it, when a folder is what's being picked
    private void TypedPath(string typed)
    {
        string before = folder;
        if (kind == UIBrowse.Folder && PlatformPaths.Same(typed.Trim().Trim('"'), folder))
            Accept();
        else
            Typed(typed);

        // Gone there, the keyboard's back on the name and the crumbs say where it is again. A path that led nowhere
        // stays in the box to be put right
        if (folder != before && !answer.Task.IsCompleted && path.IsFocused)
            name.Focus();
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
            GoTo(at);
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

        GoTo(Path.GetDirectoryName(Path.GetFullPath(at))!);
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
            AcceptFolder();
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
            GoTo(full);
            name.Text = string.Empty;
            return;
        }

        if (kind == UIBrowse.Open)
        {
            // A few letters of a row, the row they picked as they were typed
            if (!File.Exists(full) && Sought(typed) is { } row)
            {
                if (FolderOf(row) is not null)
                    name.Text = string.Empty;
                Activated(row);
                return;
            }

            if (!File.Exists(full))
            {
                Say($"there's no {typed} here");
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

    /// <summary>
    /// Choose in a folder's dialog, or Enter. A folder named (in the one shown, a path, or a few letters of one) is gone
    /// into, and with no name it's the folder picked in the list, or the one shown. Enter twice goes in and takes it.
    /// </summary>
    private void AcceptFolder()
    {
        string typed = name.Text.Trim();
        if (typed.Length == 0)
        {
            string chosen = entries.Index >= 0 && FolderOf(entries.Value) is { } picked && entries.Value != UP ? picked : folder;
            Finish(chosen);
            return;
        }

        string full = Path.GetFullPath(Path.IsPathRooted(typed) ? typed : Path.Combine(folder, typed));
        if (Directory.Exists(full))
            GoTo(full);
        else if (Sought(typed) is { } row && FolderOf(row) is { } inside)
            GoTo(inside);
        else
        {
            Say($"there's no folder {typed} here");
            return;
        }

        name.Text = string.Empty;
    }

    // What the name along the bottom says while nothing's in it
    private string NamePlaceholder => kind switch
    {
        UIBrowse.Save => "The file's name",
        UIBrowse.Folder => "A folder in here, or a path",
        _ => "A name, or a path"
    };

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
        name.Text = string.Empty;
        name.Placeholder = "The new folder's name, Enter makes it";

        // The button makes it while it's being named, Open said it would open something
        accept.Label = "Create";
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

        StopNaming();
        GoTo(made);
    }

    // Helper method to put the name along the bottom back to what it was before New folder
    private void StopNaming()
    {
        naming = false;
        name.Text = nameBefore ?? string.Empty;
        name.Placeholder = NamePlaceholder;
        accept.Label = AcceptLabel;
        Say(string.Empty);
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

    /// <summary>A key pressed with the browser having the keyboard, one of <see cref="UIKeyboard"/>'s codes or a letter.</summary>
    public void Key(char key) => name.OnTextInput(key);

    /// <summary>The row picked in the list, null for none.</summary>
    public string? Selected => entries.Index >= 0 ? entries.Value : null;

    /// <summary>What the list says under its rows, a folder with nothing in it, empty while there's something.</summary>
    public string Note => entries.Note;

    /// <summary>A file's size and when it was changed as its row says them, nothing for a row it doesn't know.</summary>
    public IReadOnlyList<string> DetailsOf(string item) => details.TryGetValue(item, out string[]? said) ? said : [];

    /// <summary>The crumbs along the top, the drive or / first and the folder shown last.</summary>
    public IReadOnlyList<string> Crumbs => crumbs.Parts;

    /// <summary>A crumb clicked, by where it is along the top.</summary>
    public void PressCrumb(int index) => crumbs.OnPressed?.Invoke(index);

    /// <summary>Back pressed, and Forward.</summary>
    public void GoBack() => Back();

    public void GoForward() => Forward();

    /// <summary>Which of the filters along the bottom is picked, by what it says.</summary>
    public string Filter
    {
        get => filter.Value;
        set
        {
            filter.Value = value;
            Show(folder);
        }
    }

    private void Say(string text) => said.Text = text;

    private void Finish(string? picked)
    {
        if (!answer.TrySetResult(picked))
            return;

        Closed();
        previousBackward = [.. backward];

        // The keyboard back to whoever had it, unless it was taken somewhere else while the window was up
        bool hadIt = false;
        for (UIComponent? at = Window.Module?.Compositor.Focus; at is not null; at = at.Parent)
            hadIt |= at == Window;

        Window.OnClosed = null;
        Window.Parent?.Remove(Window);
        if (hadIt && focusBefore is { Module: not null } before)
            before.Focus();
    }
}
