using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

/// <summary>
/// What the editor was started with, see <see cref="Program"/> for how each of them is asked for.
/// </summary>
/// <param name="Open">A layout to start with.</param>
/// <param name="Workspace">The folder layouts are opened from and saved to until another one is browsed to.</param>
/// <param name="SelfTest">Whether the editor works through itself with a scripted pointer and prints how that went.</param>
/// <param name="Wheel">Whether the self-test waits for somebody to turn the real mouse wheel.</param>
/// <param name="Check">Layouts (or folders of them) to open, test and report on rather than edit.</param>
/// <param name="Exit">Whether the editor closes once its self-test or its checks are done.</param>
/// <param name="Exercise">Whether the layouts that are checked are changed, saved, opened again and put back as well.</param>
/// <param name="Scale">How big the editor draws itself, null for what it was left at the last time.</param>
/// <param name="Browse">Whether the editor starts by asking which layout to open.</param>
internal sealed record HexOptions(string? Open, string Workspace, bool SelfTest, bool Wheel, string[] Check, bool Exit, bool Exercise = false, float? Scale = null, bool Browse = false);

/// <summary>
/// The editor. Its own UI is a layout like the ones it edits (Assets/hex/editor.hor), loaded from its file and
/// tied to the code here by the names of its parts. The layouts being edited each live in a module of their own
/// that is drawn into the canvas, so what is on screen is the real thing: the same components, laid out by the
/// same code, as when a game loads the file.
/// There are two UIs on screen for that reason: the editor's own, in a plain font that reads well small, and
/// the one the layouts are in (the stage), which looks the way a game would show them.
/// </summary>
internal sealed partial class HexScene : Scene
{
    public const string EDITOR_LAYOUT = "Assets/hex/editor.hor";

    // The same art as everywhere else with a font that is made to be read small, see the file itself.
    private const string PLAIN_SKIN = "skin_plain.hor";

    // Where the editor keeps how big it was last asked to draw itself, next to its executable.
    private const string SCALE_FILE = "hex_ui_scale.txt";

    // Where it keeps the layouts that were worked on last (one path a line, the latest first), and how many of them
    private const string RECENT_FILE = "hex_recent.txt";
    private const int MAX_RECENT = 8;

    // The screen the layouts being edited are made for, scaled to whatever room the canvas has.
    private static readonly Vector2 DesignSize = new(1600, 900);
    private static readonly UIRect DesignScreen = new(DesignSize / -2.0f, DesignSize / 2.0f);

    // How big the editor can draw itself. Text is at its sharpest at the whole ones.
    private static readonly float[] Scales = [0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f];

    // The least the parts of the editor that take whatever room is left are given, and how much of the height
    // the canvas gets at most.
    private const float MIN_FLEXIBLE = 80;
    private const float MIN_CENTER = 240;
    private const float CANVAS_SHARE = 0.64f;

    // How long a layout has to be left alone before how it is now is a step to come back to
    private const float SETTLE_TIME = 0.3f;

    // What can be added, in the order the buttons for it come.
    private static readonly string[] Palette =
    [
        "stack", "grid", "panel", "scroll", "label", "button", "toggle", "textbox", "number_box", "selector",
        "dropdown", "slider", "color_picker", "progress_bar", "image"
    ];

    // The properties that are one of a few words, and the words.
    private static readonly string[] Origins = ["center", "top_left", "top", "top_right", "left", "right", "bottom_left", "bottom", "bottom_right"];
    private static readonly Dictionary<string, string[]> Choices = new()
    {
        ["anchor"] = Origins,
        ["pivot"] = Origins,
        ["align"] = Origins,
        ["fill"] = ["none", "horizontal", "vertical", "both"],
        ["direction"] = ["vertical", "horizontal"],
        ["intro"] = ["none", "pop", "fade", "slide"]
    };

    // The numbers that have a slider next to them, and what its ends are.
    private static readonly Dictionary<string, (float Min, float Max)> Ranges = new()
    {
        ["progress"] = (0.0f, 1.0f),
        ["intro_time"] = (0.0f, 2.0f),
        ["intro_delay"] = (0.0f, 2.0f),
        ["stagger"] = (0.0f, 0.5f)
    };

    private const float EDITOR_WIDTH = 252;
    private const float EDITOR_HEIGHT = 28;
    private const float EDITOR_TEXT = 0.125f;
    private const float PICKER_HEIGHT = 112;

    private static readonly Vector4 DimColor = new(0.58f, 0.6f, 0.66f, 1.0f);
    private static readonly Vector4 FolderColor = new(0.0f, 0.86f, 1.0f, 1.0f);
    private static readonly Vector4 ErrorColor = new(1.0f, 0.45f, 0.4f, 1.0f);

    public override Camera ActiveCamera { get; protected set; }

    private readonly HexOptions options;
    private readonly Camera2D camera;
    private readonly UICompositor compositor, stage;
    private readonly UIModule chrome;
    private readonly UILayout layout;

    // The parts of the editor's own layout, found by name
    private readonly TextBox nameBox;
    private readonly StackPanel root, toolbar, body, left, center, right;
    private readonly StackPanel tabs, tree, inspector, layers;
    private readonly MenuBar menu;
    private readonly HexLayoutDebugger layoutDebugger;
    private Menu fileMenu = null!;
    private readonly Panel canvas;
    private readonly ScrollPanel treeScroll, inspectorScroll, codeScroll;
    private readonly Label code, status, treeTitle;

    // The layouts that were opened or saved last, the latest first
    private readonly List<string> recent = [];

    // Whether the stage is showing its layouts in the font of the editor, which it does for the editor's own
    private bool stagePlain;

    // A dialog of Windows that is open, and what to do with the file that is picked in it
    private Task<string?>? dialog;
    private Action<string>? dialogPicked;

    // A change that hasn't been noted as a step to come back to yet, and how long it has to rest still
    private bool recordPending;
    private float settleTimer;
    private bool undoKeyWasDown, redoKeyWasDown;

    private readonly List<HexDocument> documents = [];
    private HexDocument document = null!;

    // What has to be put together again before the next frame, so a click never rebuilds what it is in the middle of
    private bool tabsDirty = true, treeDirty = true, inspectorDirty = true, codeDirty = true;

    // Opening a layout: the list on the left shows the files of a folder rather than the components of the layout
    private bool browsing;
    private string browseDirectory;

    // What the lists on screen are showing right now, by what they stand for
    private readonly List<Button> paletteButtons = [];
    private readonly List<(string Path, bool IsDirectory, Button Row)> fileRows = [];
    private readonly Dictionary<UIComponent, Button> treeRows = [];
    private readonly Dictionary<string, Button> layerRows = [];
    private readonly List<Button> tabButtons = [];
    private readonly Dictionary<string, UIComponent> editors = [];

    // Dragging a component around the canvas
    private bool pointerWasDown;
    private bool dragging;
    private Vector2 dragLast;

    // Layouts that are only opened to be tested, and how that went
    private bool checksDone;

    public HexScene(HexOptions options)
    {
        this.options = options;
        browseDirectory = Path.GetFullPath(options.Workspace);

        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // The editor first and the layouts on top of it: the canvas is a hole in the editor they are seen through
        compositor = AddComponent(new UICompositor(camera, UICompositor.DEFAULT_SKIN_DIRECTORY, PLAIN_SKIN));
        stage = AddComponent(new UICompositor(camera));

        // Bootstrapping: the editor is made of the same thing it makes
        chrome = compositor.CreateModule();
        chrome.ShowInLayoutDebugger = false;
        layout = chrome.LoadLayout(EDITOR_LAYOUT);

        root = layout.Get<StackPanel>("root");
        toolbar = layout.Get<StackPanel>("toolbar");
        body = layout.Get<StackPanel>("body");
        left = layout.Get<StackPanel>("left");
        center = layout.Get<StackPanel>("center");
        right = layout.Get<StackPanel>("right");
        codeScroll = layout.Get<ScrollPanel>("code_scroll");
        menu = layout.Get<MenuBar>("menu");
        layers = layout.Get<StackPanel>("layers");

        nameBox = layout.Get<TextBox>("name_box");
        tabs = layout.Get<StackPanel>("tabs");
        tree = layout.Get<StackPanel>("tree");
        treeTitle = layout.Get<Label>("tree_title");
        treeScroll = layout.Get<ScrollPanel>("tree_scroll");
        inspector = layout.Get<StackPanel>("inspector");
        inspectorScroll = layout.Get<ScrollPanel>("inspector_scroll");
        canvas = layout.Get<Panel>("canvas");
        code = layout.Get<Label>("code");
        status = layout.Get<Label>("status");

        layoutDebugger = new HexLayoutDebugger(stage);

        recent.AddRange(ReadRecent());
        BuildMenus();

        SetScale(options.Scale ?? ReadScale() ?? ScaleFor(Engine.WindowManager.ViewportSize), remember: false);

        nameBox.OnChanged = text =>
        {
            document.Name = text.Length > 0 ? text : "layout";
            tabsDirty = codeDirty = true;
        };

        BuildPalette();
        Show(NewDocument());

        if (options.Open is not null)
            Open(options.Open);

        if (options.SelfTest)
            StartSelfTest();
        else if (options.Browse)
            OpenPressed();
    }

    private void BuildPalette()
    {
        // One button per kind of component, each made from the template the palette names
        var items = layout.Populate("palette", Palette.Length);

        for (int i = 0; i < items.Count; i++)
        {
            string kind = Palette[i];
            var button = items[i].Get<Button>("button");

            button.Label = kind.Replace('_', ' ');
            button.OnPressed = () => AddComponentOf(kind);
            paletteButtons.Add(button);
        }
    }

    /* The menu bar: everything there is to do that isn't done to something on screen directly */

    private void BuildMenus()
    {
        fileMenu = menu.AddMenu("File");
        RebuildFileMenu();

        Menu edit = menu.AddMenu("Edit");
        edit.Add("Undo", Undo, "Ctrl+Z").IsEnabled = () => document.CanUndo || recordPending;
        edit.Add("Redo", Redo, "Ctrl+Y").IsEnabled = () => document.CanRedo;
        edit.AddSeparator();
        edit.Add("Move up", () => MoveSelected(-1)).IsEnabled = () => document.Selected is not null;
        edit.Add("Move down", () => MoveSelected(1)).IsEnabled = () => document.Selected is not null;
        edit.Add("Delete", DeleteSelected).IsEnabled = () => document.Selected is not null;

        Menu view = menu.AddMenu("View");
        view.Add("Play intros", () => document.PlayIntros());
        view.AddSeparator();
        layoutDebugger.AddTo(view, () => Say(layoutDebugger.IsOn ? "layout debugger on: padding is green, gaps are orange" : "layout debugger off"));
        view.AddSeparator();
        view.Add("Show all layers", ShowAllLayers).IsEnabled = () => document.Module.HiddenLayers.Count > 0;
        view.AddSeparator();

        // The sizes it can be are the ones the editor knows how to lay itself out at
        foreach (float scale in Scales)
            view.Add(DescribeScale(scale), () => SetScale(scale, remember: true)).IsChecked = () => compositor.Scale == scale;
    }

    /// <summary>
    /// Puts the file menu together: what it always has, and under that the layouts that were worked on last.
    /// Again whenever those change.
    /// </summary>
    private void RebuildFileMenu()
    {
        fileMenu.Clear();

        fileMenu.Add("New", () => Show(NewDocument()));
        fileMenu.Add("Open...", OpenPressed);
        fileMenu.AddSeparator();
        fileMenu.Add("Save", Save);
        fileMenu.Add("Save as...", SaveAs);
        fileMenu.Add("Close", Close);

        if (recent.Count > 0)
        {
            fileMenu.AddSeparator();

            for (int i = 0; i < recent.Count; i++)
            {
                // Numbered, two of them can well have the same name in different folders
                string path = recent[i];
                fileMenu.Add($"{i + 1}  {Path.GetFileName(path)}", () => Open(path));
            }

            fileMenu.Add("Forget these", ForgetRecent);
        }

        fileMenu.AddSeparator();
        fileMenu.Add("The editor's own UI", () => Open(EDITOR_LAYOUT));
    }

    /* The layouts that were worked on last */

    private static string RecentPath => Path.Combine(AppContext.BaseDirectory, RECENT_FILE);

    // The editor testing itself or checking layouts starts out the same every time, and leaves nothing behind
    private bool KeepsRecent => !options.SelfTest && options.Check.Length == 0;

    private List<string> ReadRecent()
    {
        if (!KeepsRecent || !File.Exists(RecentPath))
            return [];

        try
        {
            // Only the ones that are still there
            return [.. File.ReadAllLines(RecentPath).Where(line => line.Length > 0 && File.Exists(line)).Distinct().Take(MAX_RECENT)];
        }
        catch (Exception)
        {
            // Not knowing what was open last time is no reason not to start
            return [];
        }
    }

    /// <summary>Helper to note a layout as the one that was worked on last, which puts it at the top of the list.</summary>
    private void Remember(string path)
    {
        if (!KeepsRecent || SameDirectory(path, EDITOR_LAYOUT))
            return;

        string full = Path.GetFullPath(path);
        recent.RemoveAll(known => string.Equals(known, full, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, full);

        if (recent.Count > MAX_RECENT)
            recent.RemoveRange(MAX_RECENT, recent.Count - MAX_RECENT);

        WriteRecent();
    }

    private void ForgetRecent()
    {
        recent.Clear();
        WriteRecent();
    }

    private void WriteRecent()
    {
        RebuildFileMenu();

        try
        {
            File.WriteAllLines(RecentPath, recent);
        }
        catch (Exception e)
        {
            Say($"couldn't keep the list of layouts for next time: {e.Message}", error: true);
        }
    }

    /* Documents */

    private HexDocument NewDocument()
    {
        string name = "layout";
        for (int number = 2; documents.Exists(other => other.Name == name); number++)
            name = $"layout{number}";

        var created = new HexDocument(name, stage.CreateModule()) { FallbackDirectory = browseDirectory };

        // Laid out as if the canvas were the screen it is made for, and kept inside of it
        created.Module.Viewport = DesignScreen;
        created.Module.Clip = DesignScreen;

        documents.Add(created);
        return created;
    }

    private void Show(HexDocument shown)
    {
        // Whatever was being done to the layout that is put away is a step back in it, not in the next one
        if (recordPending && document is not null)
            document.Record();
        recordPending = false;

        document = shown;

        // The editor's own layouts are made for the font of the editor, everything else for the one games have
        bool plain = shown.Path is { } path && SameDirectory(path, EDITOR_LAYOUT);
        if (plain != stagePlain)
        {
            stagePlain = plain;
            stage.SetSkin(UICompositor.DEFAULT_SKIN_DIRECTORY, plain ? PLAIN_SKIN : UICompositor.DEFAULT_SKIN_FILE);
        }

        // Only the layout that is being worked on is on screen
        foreach (var other in documents)
            other.Module.Enabled = other == shown;

        nameBox.Text = shown.Name;
        stage.Highlighted = shown.Selected;
        browsing = false;
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;
    }

    /// <summary>
    /// Opens a layout file in a tab of its own, and checks that writing it back out and reading that gives the
    /// same layout again: if it doesn't, saving would change it.
    /// </summary>
    /// <returns>Whether it opened and writes back the same.</returns>
    public bool Open(string path)
    {
        if (!File.Exists(path))
        {
            Say($"there is no '{path}'", error: true);
            return false;
        }

        // Somebody who opens the same file twice wants to see it, not have it twice
        string full = Path.GetFullPath(path);
        if (documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path) == full) is { } already)
        {
            Show(already);
            Say($"{already.Name} is open already");
            return true;
        }

        // Reusing the empty layout the editor starts with saves a tab
        HexDocument target = document.Path is null && !document.Module.Root.Children.Any() ? document : NewDocument();
        target.Name = Path.GetFileNameWithoutExtension(path);
        target.Path = path;

        var (success, message) = target.Load(File.ReadAllText(path), path);
        Show(target);

        if (!success)
        {
            target.Path = null;
            Console.WriteLine($"[Hex] '{path}' didn't open: {message}");
            Say(message, error: true);
            return false;
        }

        Remember(path);

        bool faithful = CheckRoundTrip(target);
        int count = target.Walk().Count();

        Console.WriteLine($"[Hex] Opened '{path}': {count} components, {target.PreviewCount} stand-in items, round trip {(faithful ? "ok" : "DIFFERS")}.");

        if (!faithful)
            Say($"opened {target.Name}, but it doesn't write back the same", error: true);
        else if (message.Length > 0)
            Say(message, error: true);
        else
            Say($"opened {target.Name}: {count} components, writes back the same");

        return faithful;
    }

    /// <summary>
    /// Helper to test that the code written for a layout builds that same layout: it is loaded into a module
    /// nobody sees and written out again, which has to give the same code.
    /// </summary>
    private bool CheckRoundTrip(HexDocument checkedDocument)
    {
        string written = checkedDocument.GenerateCode();

        var scratch = new HexDocument(checkedDocument.Name, stage.CreateModule());
        scratch.Module.Enabled = false;

        try
        {
            return scratch.Load(written).Success && scratch.GenerateCode() == written;
        }
        finally
        {
            stage.RemoveModule(scratch.Module);
        }
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(a)), Path.GetDirectoryName(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /* Opening and saving. Windows asks where, the editor testing itself (which can't click in a dialog of
       Windows) uses the list on the left to open and its workspace to save. */

    private nint WindowHandle => Engine.WindowManager.Window.Native?.Win32?.Hwnd ?? 0;

    private void OpenPressed()
    {
        if (options.SelfTest)
        {
            ToggleBrowsing();
            return;
        }

        Ask(HexDialogs.Open(browseDirectory, WindowHandle), path =>
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;
            Open(path);
        });
    }

    private void Save()
    {
        // Back to where it came from, unless it has been given another name since: that is saving a copy
        if (document.Path is { } known && Path.GetFileNameWithoutExtension(known) == document.Name)
            SaveTo(known);
        else if (document.Path is null && !options.SelfTest)
            SaveAs();
        else
            SaveTo(Path.Combine(document.Path is { } copyOf ? Path.GetDirectoryName(Path.GetFullPath(copyOf))! : browseDirectory, document.Name + ".hor"));
    }

    private void SaveAs()
    {
        if (options.SelfTest)
        {
            SaveTo(Path.Combine(browseDirectory, document.Name + ".hor"));
            return;
        }

        HexDocument saved = document;
        string directory = saved.Path is { } known ? Path.GetDirectoryName(Path.GetFullPath(known))! : browseDirectory;

        Ask(HexDialogs.Save(directory, saved.Name + ".hor", WindowHandle), path =>
        {
            // The dialog was open for a while, the layout it was about may not be the one on screen any more
            if (!documents.Contains(saved))
                return;

            Show(saved);

            // A layout goes by the name of its file
            saved.Name = Path.GetFileNameWithoutExtension(path);
            nameBox.Text = saved.Name;
            browseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? browseDirectory;

            SaveTo(path);
        });
    }

    /// <summary>Helper to show a dialog of Windows and do something with the file that is picked in it, one at a time.</summary>
    private void Ask(Task<string?> asked, Action<string> picked)
    {
        if (dialog is not null)
        {
            Say("there is a dialog open already", error: true);
            return;
        }

        dialog = asked;
        dialogPicked = picked;
    }

    private void UpdateDialog()
    {
        if (dialog is not { IsCompleted: true } answered)
            return;

        Action<string>? picked = dialogPicked;
        dialog = null;
        dialogPicked = null;

        if (answered.IsFaulted)
            Say($"the dialog didn't open: {answered.Exception?.InnerException?.Message}", error: true);
        else if (answered.Result is { } path)
            picked?.Invoke(path);
    }

    private void SaveTo(string path)
    {
        // Saving the editor over itself with something broken would be the end of it, that is only ever saved as a copy
        if (SameDirectory(path, EDITOR_LAYOUT))
            path = Path.Combine(Path.GetFullPath(options.Workspace), document.Name + "_edited.hor");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, document.GenerateCode());

            document.Path = path;

            // Its templates are looked for next to it, which may be somewhere else now
            document.RefreshPreviews();
            tabsDirty = treeDirty = true;
            Remember(path);

            Console.WriteLine($"[Hex] Saved '{path}'.");
            Say($"saved {path}");
        }
        catch (Exception e)
        {
            Say($"couldn't save {path}: {e.Message}", error: true);
        }
    }

    private void Close()
    {
        if (stage.Highlighted == document.Selected)
            stage.Highlighted = null;

        stage.RemoveModule(document.Module);
        documents.Remove(document);
        recordPending = false;

        Show(documents.Count > 0 ? documents[^1] : NewDocument());
    }

    /* Editing */

    private void AddComponentOf(string kind)
    {
        // Into whatever is selected if that is something things go into, otherwise next to it
        UIComponent? parent = document.Selected switch
        {
            Panel container => container,
            { Parent: { } owner } when owner != document.Module.Root => owner,
            _ => null
        };

        if (document.Add(kind, parent) is { } added)
        {
            browsing = false;
            Select(added);
            treeDirty = codeDirty = true;
        }
    }

    private void DeleteSelected()
    {
        if (document.Selected is not { } selected)
            return;

        document.Remove(selected);
        Select(null);
        treeDirty = codeDirty = true;
    }

    private void MoveSelected(int by)
    {
        if (document.Selected is not { Parent: { } parent } selected)
            return;

        int index = 0;
        foreach (var sibling in parent.Children)
        {
            if (sibling == selected)
                break;
            index++;
        }

        parent.MoveChild(selected, index + by);
        treeDirty = codeDirty = true;
    }

    private void Select(UIComponent? component)
    {
        document.Selected = component;

        // The UI marks it out itself, the same way the layout debugger marks what the pointer is over
        stage.Highlighted = component;
        treeDirty = inspectorDirty = true;
    }

    /* Undoing */

    private void Undo()
    {
        if (document.Undo())
            Restored("undone");
        else
            Say("nothing to undo");
    }

    private void Redo()
    {
        if (document.Redo())
            Restored("redone");
        else
            Say("nothing to redo");
    }

    private void Restored(string what)
    {
        // The layout was built again from what it was, everything on screen that is about it is out of date
        stage.Highlighted = document.Selected;
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;

        Say($"{what}, {document.UndoCount} more to undo");
    }

    /// <summary>
    /// Notes the layout as a step to come back to once a change has been left alone for a moment with the
    /// pointer up, and listens for control with Z and with Y.
    /// </summary>
    private void UpdateHistory(float dt)
    {
        if (recordPending && !compositor.Pointer.Down && (settleTimer -= dt) <= 0.0f)
        {
            recordPending = false;
            document.Record();
        }

        var keyboard = Engine.InputManager.KeyboardManager;
        bool control = keyboard.IsKeyDown(Silk.NET.Input.Key.ControlLeft) || keyboard.IsKeyDown(Silk.NET.Input.Key.ControlRight);
        bool undoKey = control && keyboard.IsKeyDown(Silk.NET.Input.Key.Z);
        bool redoKey = control && keyboard.IsKeyDown(Silk.NET.Input.Key.Y);

        if (undoKey && !undoKeyWasDown) Undo();
        if (redoKey && !redoKeyWasDown) Redo();

        undoKeyWasDown = undoKey;
        redoKeyWasDown = redoKey;
    }

    /* How big the editor draws itself */

    private static string DescribeScale(float scale) => $"{scale * 100:0}%";

    /// <summary>The size a window is best read at when nobody has said: bigger on a bigger screen.</summary>
    private static float ScaleFor(Vector2 window) => window.Y switch
    {
        >= 2000 => 2.0f,
        >= 1400 => 1.5f,
        >= 1000 => 1.25f,
        _ => 1.0f
    };

    private static string ScalePath => Path.Combine(AppContext.BaseDirectory, SCALE_FILE);

    private float? ReadScale()
    {
        // The editor testing itself starts out the same every time
        if (options.SelfTest || !File.Exists(ScalePath))
            return null;

        return float.TryParse(File.ReadAllText(ScalePath), System.Globalization.CultureInfo.InvariantCulture, out float saved) ? saved : null;
    }

    /// <summary>
    /// Sets how big the editor and the layouts in it are drawn. The editor makes room by itself: the lists,
    /// the canvas and the code get whatever the window has left at that size, see <see cref="FitChrome"/>.
    /// </summary>
    /// <param name="remember">Whether this is what the editor starts with from now on.</param>
    private void SetScale(float scale, bool remember)
    {
        scale = Scales.MinBy(known => MathF.Abs(known - scale));

        // The two are one screen as far as anybody looking at it is concerned
        compositor.Scale = stage.Scale = scale;

        if (!remember || options.SelfTest)
            return;

        try
        {
            File.WriteAllText(ScalePath, scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e)
        {
            Say($"couldn't keep the scale for next time: {e.Message}", error: true);
        }
    }

    /// <summary>
    /// Gives the parts of the editor that have no size of their own the room the window has left: the list on
    /// the left and the inspector the height their columns have to spare, the canvas and the code the width
    /// between the two and the height between them. Everything else keeps the size its layout gives it.
    /// </summary>
    private void FitChrome()
    {
        UIRect screen = chrome.Root.Bounds;
        if (screen.IsEmpty || toolbar.Bounds.IsEmpty)
            return;

        float skinSpacing = compositor.Skin?.Spacing ?? 0.0f;
        float rowGap = root.Spacing >= 0.0f ? root.Spacing : skinSpacing;
        float columnGap = body.Spacing >= 0.0f ? body.Spacing : skinSpacing;

        float height = screen.Height - root.Padding.Total.Y - toolbar.Bounds.Height - rowGap;
        float leftWidth = treeScroll.Size.X;
        float rightWidth = inspectorScroll.Size.X;
        float centerWidth = MathF.Max(MIN_CENTER, screen.Width - root.Padding.Total.X - columnGap * 2.0f - leftWidth - rightWidth);

        // What a column needs for everything in it that isn't given its height here, as it was last laid out
        float Spoken(StackPanel column, params UIComponent[] flexible) =>
            column.Bounds.Height - flexible.Sum(part => part.Bounds.Height);

        treeScroll.Size = new Vector2(leftWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(left, treeScroll)));
        inspectorScroll.Size = new Vector2(rightWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(right, inspectorScroll)));
        status.Size = new Vector2(rightWidth, status.Size.Y);

        // The canvas is the shape of the screen the layouts are made for, as long as that leaves room for the code
        float canvasHeight = MathF.Min(centerWidth * DesignSize.Y / DesignSize.X, height * CANVAS_SHARE);

        canvas.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, canvasHeight));
        codeScroll.Size = new Vector2(centerWidth, MathF.Max(MIN_FLEXIBLE, height - Spoken(center, canvas, codeScroll) - canvas.Size.Y));
    }

    private void ToggleBrowsing()
    {
        browsing = !browsing;
        treeDirty = true;

        if (browsing)
            Say($"files of {browseDirectory}");
    }

    private void Say(string message, bool error = false)
    {
        status.Text = message.Replace('[', '(').Replace(']', ')');
        status.Color = error ? ErrorColor : DimColor;
    }

    /* Every frame */

    public override void UpdateState(float dt)
    {
        // A unit of the camera per pixel of the window, whatever the window is resized to: the UI does the scaling
        Vector2 viewport = Engine.WindowManager.ViewportSize;
        if (viewport != camera.ViewSize && viewport.X > 0 && viewport.Y > 0)
            camera.ViewSize = viewport;

        if (options.Check.Length > 0 && !checksDone)
            RunChecks();

        UpdateDialog();
        FitChrome();
        PlaceCanvas();
        UpdateCanvasPointer();
        UpdateHistory(dt);

        // Whatever changed the code changed the layout
        if (codeDirty)
        {
            recordPending = true;
            settleTimer = SETTLE_TIME;
        }

        if (tabsDirty) RebuildTabs();
        if (treeDirty) RebuildTree();
        if (inspectorDirty) RebuildInspector();

        // Square brackets would be read as an icon to draw, which is not what the ones in code are
        if (codeDirty) code.Text = document.GenerateCode().Replace("[", "(").Replace("]", ")");
        tabsDirty = treeDirty = inspectorDirty = codeDirty = false;

        base.UpdateState(dt);

        UpdateSelfTest(dt);
    }

    /// <summary>Fits the design screen of the layout into the canvas, wherever the editor's layout has put that.</summary>
    private void PlaceCanvas()
    {
        UIRect area = canvas.Bounds;
        if (area.IsEmpty)
            return;

        float scale = MathF.Min(area.Width / DesignScreen.Width, area.Height / DesignScreen.Height);

        // In the units of the editor's layout, the UI scales the two of them together
        document.Module.Scale = new Vector2(scale);
        document.Module.Position = area.Center;
    }

    /// <summary>Clicking in the canvas selects what is there, dragging moves it.</summary>
    private void UpdateCanvasPointer()
    {
        UIPointer pointer = compositor.Pointer;
        bool pressed = pointer.Down && !pointerWasDown;
        bool released = !pointer.Down && pointerWasDown;
        pointerWasDown = pointer.Down;

        // Where the pointer is in the layout that is being edited, however big that is drawn
        Vector2 inLayout = document.Module.ToLocal(pointer.Position);

        if (pressed && canvas.Bounds.Contains(chrome.ToLocal(pointer.Position)))
        {
            // What stands in for the items of a container is the container as far as selecting goes
            Select(document.OwnerOf(document.Module.FindAt(pointer.Position)));
            dragging = document.Selected is not null;
            dragLast = inLayout;
        }
        else if (dragging && pointer.Down && document.Selected is { } selected)
        {
            Vector2 moved = inLayout - dragLast;
            if (moved != Vector2.Zero)
            {
                selected.Position += moved;
                dragLast = inLayout;
                codeDirty = true;
            }
        }

        if (released && dragging)
        {
            dragging = false;

            // The numbers in the inspector are from before the drag
            inspectorDirty = true;
        }
    }

    private void RebuildTabs()
    {
        tabButtons.Clear();
        var items = layout.Populate(tabs, documents.Count);

        for (int i = 0; i < items.Count; i++)
        {
            HexDocument shown = documents[i];
            var tab = items[i].Get<Button>("tab");

            tab.Label = shown.Name;
            tab.Selected = shown == document;
            tab.OnPressed = () => Show(shown);
            tabButtons.Add(tab);
        }
    }

    private void RebuildTree()
    {
        treeRows.Clear();
        fileRows.Clear();

        if (browsing)
        {
            RebuildFiles();
            return;
        }

        treeTitle.Text = "Layout";

        var components = document.Walk().ToList();
        var items = layout.Populate(tree, components.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var (component, depth) = components[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            // The text steps in with how deep in the layout the component is
            caption.Text = $"{document.NameOf(component)}  ({UIModule.KindOf(component)})";
            caption.Position = new Vector2(10 + depth * 14, 0);
            caption.Color = component.Visible && !component.IsHiddenByLayer ? null : DimColor;

            row.Selected = component == document.Selected;
            row.OnPressed = () => Select(component);
            treeRows[component] = row;
        }

        if (components.Count == 0)
            tree.Add(new Label("nothing yet, add something") { Anchor = Origin.Left, TextScale = 0.18f, Color = DimColor });

        RebuildLayers();
    }

    /* Layers: parts of a layout that are shown and hidden together, so what is laid over something else can be put
       out of the way while that is worked on. Which layer a component is on is one of its properties and goes into
       the file, which of them are hidden right now is only how the editor is looking at the layout. */

    private void RebuildLayers()
    {
        layerRows.Clear();

        var names = document.Module.Layers;
        var items = layout.Populate(layers, names.Count);

        for (int i = 0; i < items.Count; i++)
        {
            string name = names[i];
            bool hidden = document.Module.IsLayerHidden(name);

            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = hidden ? $"{name}  (hidden)" : name;
            caption.Color = hidden ? DimColor : null;

            row.OnPressed = () => ToggleLayer(name);
            layerRows[name] = row;
        }

        if (names.Count == 0)
        {
            layers.Add(new Label("none yet: a component gets one\nin its properties, under layer")
            {
                Anchor = Origin.Left,
                Align = Origin.TopLeft,
                TextScale = EDITOR_TEXT,
                Color = DimColor
            });
        }
    }

    private void ToggleLayer(string name)
    {
        bool show = document.Module.IsLayerHidden(name);
        document.Module.SetLayerVisible(name, show);

        // The tree greys out what is out of sight
        treeDirty = true;
        Say(show ? $"showing the layer {name}" : $"hid the layer {name}, it is still in the layout and in the file");
    }

    private void ShowAllLayers()
    {
        document.Module.ShowAllLayers();
        treeDirty = true;
    }

    /// <summary>
    /// Helper to list the folder a layout is being opened from: the way up, the folders in it and its layouts.
    /// </summary>
    private void RebuildFiles()
    {
        treeTitle.Text = "Open a layout";
        treeScroll.Offset = 0;

        var entries = new List<(string Path, bool IsDirectory, string Text)>();
        try
        {
            if (Directory.GetParent(browseDirectory) is { } parent)
                entries.Add((parent.FullName, true, "../"));

            foreach (string directory in Directory.GetDirectories(browseDirectory).Order())
                entries.Add((directory, true, Path.GetFileName(directory) + "/"));

            foreach (string file in Directory.GetFiles(browseDirectory, "*.hor").Order())
                entries.Add((file, false, Path.GetFileName(file)));
        }
        catch (Exception e)
        {
            Say($"couldn't look into {browseDirectory}: {e.Message}", error: true);
        }

        var items = layout.Populate(tree, entries.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var (path, isDirectory, text) = entries[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = text;
            caption.Color = isDirectory ? FolderColor : null;
            row.OnPressed = () => PickFile(path, isDirectory);
            fileRows.Add((path, isDirectory, row));
        }
    }

    private void PickFile(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            // New layouts are saved to wherever was looked at last
            browseDirectory = path;
            treeDirty = true;
            Say($"files of {browseDirectory}");
            return;
        }

        Open(path);
    }

    /* The inspector: one row per property of whatever is selected, each with an editor for the kind of value it is */

    private void RebuildInspector()
    {
        editors.Clear();
        foreach (var child in inspector.Children.ToArray())
            inspector.Remove(child);

        inspectorScroll.Offset = 0;

        if (document.Selected is not { } selected)
        {
            inspector.Add(new Label("select something in the canvas\nor in the layout on the left")
            {
                Anchor = Origin.Left,
                Align = Origin.TopLeft,
                TextScale = EDITOR_TEXT,
                Color = DimColor
            });
            return;
        }

        var nameEditor = new TextBox(document.NameOf(selected))
        {
            Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
            TextScale = EDITOR_TEXT,
            MaxLength = 32,
            Padding = new UIEdges(8, 6)
        };
        nameEditor.OnChanged = text =>
        {
            if (document.Rename(selected, text))
            {
                treeDirty = codeDirty = true;
                Say($"renamed to {text}");
            }
            else
            {
                Say($"'{text}' can't be its name: taken, or not a name a variable can have", error: true);
            }
        };
        AddRow("name", nameEditor);
        AddRow("kind", new Label(UIModule.KindOf(selected) ?? selected.GetType().Name)
        {
            Align = Origin.Left,
            TextScale = EDITOR_TEXT,
            Size = new Vector2(EDITOR_WIDTH, 24)
        });

        foreach (var (name, value) in HexDocument.ReadProperties(selected))
        {
            if (CreateEditor(selected, name, value) is { } editor)
                AddRow(name, editor);
        }
    }

    private void AddRow(string name, UIComponent editor)
    {
        // The row itself is the template the inspector names, the editor goes into the room it leaves for one
        var row = layout.Instantiate(inspector.Template, inspector);

        row.Get<Label>("caption").Text = name.Replace('_', ' ');
        row.Get<StackPanel>("editor").Add(editor);
        editors[name] = editor;
    }

    private UIComponent? CreateEditor(UIComponent target, string name, IRuntimeValue value)
    {
        switch (value)
        {
            case NumberValue number when Ranges.ContainsKey(name) || (name == "value" && target is Slider):
            {
                // A slider for getting there quickly and the number next to it for getting there exactly
                var (min, max) = target is Slider ranged && name == "value" ? (ranged.Min, ranged.Max) : Ranges[name];
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };

                NumberBox exact = null!;
                var slider = row.Add(new Slider
                {
                    Size = new Vector2(EDITOR_WIDTH - 72, EDITOR_HEIGHT - 6),
                    Min = min,
                    Max = max,
                    Value = number.Value
                });
                exact = row.Add(NumberEditor(68, number.Value, changed =>
                {
                    slider.Value = changed;
                    Set(target, name, new NumberValue(changed));
                }));
                exact.DragStep = (max - min) / 100.0f;

                slider.OnChanged = changed =>
                {
                    // To the hundredth, nobody wants 0.3478 seconds
                    changed = MathF.Round(changed, 2);
                    exact.Value = changed;
                    Set(target, name, new NumberValue(changed));
                };
                return row;
            }

            case NumberValue number:
                return NumberEditor(EDITOR_WIDTH, number.Value, changed => Set(target, name, new NumberValue(changed)));

            case BooleanValue boolean:
                return new Selector("no", "yes")
                {
                    Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    Value = boolean.Value ? "yes" : "no",
                    OnChanged = chosen => Set(target, name, new BooleanValue(chosen == "yes"))
                };

            case StringValue text when Choices.TryGetValue(name, out var choices):
                return new Dropdown(choices)
                {
                    Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    Value = text.Value,
                    OnChanged = chosen => Set(target, name, new StringValue(chosen))
                };

            case StringValue text:
                // A quote can't be written into a script, so one never gets as far as the component
                return new TextBox(text.Value)
                {
                    Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    MaxLength = 200,
                    Padding = new UIEdges(8, 6),
                    OnChanged = typed => Set(target, name, new StringValue(typed.Replace('"', '\'')))
                };

            case Vector2Value vector:
            {
                Vector2 current = vector.Value;
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };

                row.Add(NumberEditor((EDITOR_WIDTH - 4) / 2, current.X, x => { current.X = x; Set(target, name, new Vector2Value(current)); }));
                row.Add(NumberEditor((EDITOR_WIDTH - 4) / 2, current.Y, y => { current.Y = y; Set(target, name, new Vector2Value(current)); }));
                return row;
            }

            case Vector4Value vector:
            {
                // Colours go from 0 to 1, everything else with four numbers is edges in pixels
                bool colour = name is "color" or "tint";
                Vector4 current = vector.Value;
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };
                var numbers = new NumberBox[4];
                ColorPicker? picker = null;

                for (int i = 0; i < 4; i++)
                {
                    int component = i;
                    numbers[i] = row.Add(NumberEditor((EDITOR_WIDTH - 12) / 4, current[i], changed =>
                    {
                        current[component] = changed;
                        if (picker is not null) picker.Color = current;
                        Set(target, name, new Vector4Value(current));
                    }));
                    numbers[i].DragStep = colour ? 0.02f : 1.0f;
                }

                if (!colour)
                    return row;

                // Picked by eye on top, the numbers it comes to underneath for when they have to be exact
                var both = new StackPanel { Spacing = 4 };
                picker = both.Add(new ColorPicker
                {
                    Size = new Vector2(EDITOR_WIDTH, PICKER_HEIGHT),
                    Color = current,
                    OnChanged = picked =>
                    {
                        current = new Vector4(MathF.Round(picked.X, 3), MathF.Round(picked.Y, 3), MathF.Round(picked.Z, 3), MathF.Round(picked.W, 3));
                        for (int i = 0; i < 4; i++)
                            numbers[i].Value = current[i];

                        Set(target, name, new Vector4Value(current));
                    }
                });
                both.Add(row);
                return both;
            }

            // Handlers are functions, there is nothing to type one into
            default:
                return null;
        }
    }

    private static NumberBox NumberEditor(float width, float value, Action<float> changed) => new(value)
    {
        Size = new Vector2(width, EDITOR_HEIGHT),
        TextScale = EDITOR_TEXT,
        Padding = new UIEdges(6, 6),
        OnValueChanged = changed
    };

    private void Set(UIComponent target, string name, IRuntimeValue value)
    {
        if (HexDocument.Write(target, name, value) is { } problem)
        {
            Say(problem, error: true);
            return;
        }

        codeDirty = true;

        // The tree greys out what is hidden, and the layers are listed by what the components say they are on
        if (name is "visible" or "layer")
            treeDirty = true;

        // An entrance is easier to set up when it is played back every time something about it changes
        if (name.StartsWith("intro") || name == "stagger")
        {
            document.SettleIntros();
            target.PlayIntro();
        }

        // What a container is shown filled with is up to these two
        if (name is "template" or "preview_count" && document.RefreshPreviews() is { } missing)
            Say(missing, error: true);
    }

    /* Testing layouts rather than editing them */

    /// <summary>
    /// Opens every layout that was asked to be checked, says how each of them did and (if asked to) closes the
    /// editor with the number that didn't make it as its exit code.
    /// </summary>
    private void RunChecks()
    {
        checksDone = true;

        var files = new List<string>();
        foreach (string path in options.Check)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.hor", SearchOption.AllDirectories).Order());
            else
                files.Add(path);
        }

        int failed = 0;
        foreach (string file in files)
        {
            if (!Open(file) || (options.Exercise && !Exercise(file)))
            {
                Console.WriteLine($"[Hex check] FAIL: {file}");
                failed++;
            }
            else
            {
                Console.WriteLine($"[Hex check] pass: {file}");
            }
        }

        Console.WriteLine(options.Exercise
            ? $"[Hex check] {files.Count - failed} of {files.Count} layouts open, write back the same and come through being edited."
            : $"[Hex check] {files.Count - failed} of {files.Count} layouts open and write back the same.");

        if (options.Exit && !options.SelfTest)
            Quit(failed);
    }

    /// <summary>
    /// Puts the layout that was just opened through what somebody editing it would: something in it is moved,
    /// something is added and named, it is saved and opened again, all of that is taken back by hand and it is
    /// saved and opened once more, and then the same change is undone and redone. Every name the layout had has
    /// to still be there after each step (a program finds its parts by them), and at the end the file has to be
    /// what the editor wrote for the layout nobody had touched. The file is written over, so this is for copies.
    /// </summary>
    private bool Exercise(string file)
    {
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            if (!holds) problems.Add(what);
        }

        string untouched = document.GenerateCode();
        List<string> names = [.. document.Walk().Select(entry => document.NameOf(entry.Component))];
        string firstName = names[0];
        bool AllNamed() => names.TrueForAll(name => document.Find(name) is not null);

        // Edited: moved, and with a label of its own in the first thing that takes one
        UIComponent first = document.Find(firstName)!;
        Vector2 was = first.Position;
        Select(first);
        Set(first, "pos", new Vector2Value(was + new Vector2(12, -7)));

        UIComponent? added = document.Add("label", document.Walk().Select(entry => entry.Component).OfType<Panel>().FirstOrDefault());
        Expect(added is not null && document.Rename(added, "hex_exercise"), "a label couldn't be added and named");
        document.Record();

        string edited = document.GenerateCode();
        SaveTo(file);
        Close();

        Expect(Open(file), "the edited layout didn't open, or doesn't write back the same");
        Expect(document.GenerateCode() == edited, "the edited layout came back different from how it was saved");
        Expect(AllNamed() && document.Find("hex_exercise") is Label, "a name went missing in the edited layout");
        Expect(document.Find(firstName)?.Position == was + new Vector2(12, -7), "the move didn't make it into the file");

        // Put back by hand
        if (document.Find("hex_exercise") is { } label)
            document.Remove(label);
        if (document.Find(firstName) is { } moved)
            Set(moved, "pos", new Vector2Value(was));

        SaveTo(file);
        Close();

        Expect(Open(file), "the layout that was put back didn't open");
        Expect(document.GenerateCode() == untouched, "putting everything back didn't give the layout it started as");
        Expect(AllNamed(), "a name went missing putting everything back");

        // And once more with undo and redo, which build the layout again from what it was
        Set(document.Find(firstName)!, "pos", new Vector2Value(was + new Vector2(30, 30)));
        document.Record();
        string changed = document.GenerateCode();

        Expect(document.Undo() && document.GenerateCode() == untouched && AllNamed(), "undo didn't give the layout back");
        Expect(document.Redo() && document.GenerateCode() == changed && AllNamed(), "redo didn't make the change again");
        Expect(document.Undo() && document.GenerateCode() == untouched, "undo didn't work a second time");
        Expect(System.IO.File.ReadAllText(file) == untouched, "the file isn't the layout it started as");

        Restored("exercised");

        foreach (string problem in problems)
            Console.WriteLine($"[Hex check] {Path.GetFileName(file)}: {problem}");

        return problems.Count == 0;
    }

    private void Quit(int failures)
    {
        System.Environment.ExitCode = failures == 0 ? 0 : 1;
        Engine.WindowManager.Window.Close();
    }
}
