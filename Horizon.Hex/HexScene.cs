using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

/// <summary>
/// The editor. Its own UI is a layout like the ones it edits (Assets/hex/editor.hor), loaded from its file and
/// tied to the code here by the names of its parts. The layouts being edited each live in a module of their own
/// that is drawn into the canvas, so what is on screen is the real thing. The same components, laid out by the
/// same code, as when a game loads the file.
/// There are two UIs on screen for that reason: the editor's own, in a plain font that reads well small, and
/// the one the layouts are in (the stage), which looks the way a game would show them.
/// </summary>
internal sealed partial class HexScene : Scene
{
    public const string EDITOR_LAYOUT = "Assets/hex/editor.hor";

    // The skin of the editor itself, flat and without any art. See the file for what it looks like and why
    private const string TOOL_SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string TOOL_SKIN = "skin.hor";

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
        "dropdown", "list", "slider", "color_picker", "progress_bar", "image", "tabs", "divider", "spacer"
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
    private static readonly Vector4 FolderColor = new(0.38f, 0.62f, 1.0f, 1.0f);
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

    // What is behind the layouts in the canvas, a module of the stage as big as the canvas
    private readonly UIModule backdrop;
    private static readonly Vector4 CanvasColor = new(0.02f, 0.02f, 0.03f, 1.0f);
    private readonly ScrollPanel treeScroll, inspectorScroll, codeScroll;
    private readonly Label status, treeTitle, coords;
    private readonly CodeView code;

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
    private readonly HexShortcuts shortcuts = new();

    private readonly List<HexDocument> documents = [];
    private HexDocument document = null!;

    // What has to be put together again before the next frame, so a click never rebuilds what it is in the middle of
    private bool tabsDirty = true, treeDirty = true, inspectorDirty = true, codeDirty = true;

    // Opening a layout. The list on the left shows the files of a folder rather than the components of the layout
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
    private bool dragging, dragMoved;
    private Vector2 dragLast;

    // Something that was selected already and pressed on: it's all that's selected if it isn't dragged
    private UIComponent? clickedSelected;

    // Layouts that are only opened to be tested, and how that went
    private bool checksDone;

    public HexScene(HexOptions options)
    {
        this.options = options;
        browseDirectory = Path.GetFullPath(options.Workspace);

        camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;


        // The layouts first and the editor over them. The canvas is a hole in the editor they are seen through, so the
        // menus, lists and dialogs of the editor come out on top of them rather than under. What is dark behind the
        // layouts is a module of the stage itself, the first one, see PlaceCanvas
        stage = AddComponent(new UICompositor(camera));
        compositor = AddComponent(new UICompositor(camera, TOOL_SKIN_DIRECTORY, TOOL_SKIN));
        backdrop = stage.CreateModule();
        backdrop.ShowInLayoutDebugger = false;
        backdrop.Interactive = false;
        backdrop.AddComponent(new Panel { Fill = UIFill.Both, Color = CanvasColor });

        // Bootstrapping. The editor is made of the same thing it makes
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
        code = layout.Get<CodeView>("code");
        status = layout.Get<Label>("status");
        coords = layout.Get<Label>("coords");

        layoutDebugger = new HexLayoutDebugger(stage);

        recent.AddRange(ReadRecent());
        BuildMenus();
        BuildLayoutMenu();
        BuildShortcuts();
        BuildSelectionShortcuts();

        // A right click on the canvas that nothing of the editor took is a menu for what's there
        compositor.ContextRequested = OnContextRequested;

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

    public override void PostInit()
    {
        base.PostInit();

        // The dark the editor sits on. Its root has no colour of its own, so the layouts drawn under it show through the canvas
        Engine.GL.ClearColor(0.065f, 0.07f, 0.092f, 1.0f);
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
        // A unit of the camera per pixel of the window, whatever the window is resized to. The UI does the scaling
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

        if (codeDirty) ShowCode();

        if (tabsDirty) RebuildTabs();
        if (treeDirty) RebuildTree();
        if (inspectorDirty) RebuildInspector();

        tabsDirty = treeDirty = inspectorDirty = codeDirty = false;

        base.UpdateState(dt);

        UpdateSelfTest(dt);
    }

    /// <summary>
    /// Helper method to put the code of the layout on screen, and to work out whether it is still what is in its file.
    /// </summary>
    private void ShowCode()
    {
        string written = document.GenerateCode();
        code.Text = written;

        // The tab of a layout says so when there is something to save
        bool modified = written != document.SavedCode;
        if (modified != document.Modified)
        {
            document.Modified = modified;
            tabsDirty = true;
        }
    }

    /// <summary>
    /// Clicking in the canvas selects what is there (on top of what already is with ctrl or shift), dragging moves all
    /// of it. The wheel zooms, the middle button (or space) pans, and a click on a tab opens its page.
    /// </summary>
    private void UpdateCanvasPointer()
    {
        UIPointer pointer = compositor.Pointer;
        bool pressed = pointer.Down && !pointerWasDown;
        bool released = !pointer.Down && pointerWasDown;
        pointerWasDown = pointer.Down;

        // Where the pointer is in the editor, and in the layout that is being edited however big that is drawn
        Vector2 inChrome = chrome.ToLocal(pointer.Position);
        Vector2 inLayout = document.Module.ToLocal(pointer.Position);

        // Not over the canvas if a menu of the editor is in the way of it
        bool overCanvas = canvas.Bounds.Contains(inChrome) && chrome.Popup is null;

        // Where the pointer is in the layout, in its own units, for lining things up by number
        coords.Text = overCanvas ? $"{inLayout.X:0}, {inLayout.Y:0}" : string.Empty;

        if (UpdateCanvasView(inChrome, overCanvas, pressed))
        {
            dragging = false;
            return;
        }

        if (pressed && overCanvas)
        {
            UIComponent? under = document.Module.FindAt(pointer.Position);

            // A tab in the canvas opens its page, the way it would in a game, and selects the tabs
            if (TabUnder(under, inLayout) is var (tabs, index))
            {
                tabs.Selected = index;
                Select(document.OwnerOf(tabs) ?? tabs);
                return;
            }

            // What stands in for the items of a container is the container as far as selecting goes
            UIComponent? hit = ResolveClick(document.OwnerOf(under));

            // Pressing on something that's selected already drags all of the selection, a click without a drag
            // selects only it (see below)
            clickedSelected = hit is not null && document.IsSelected(hit) && !Modifier() ? hit : null;
            if (clickedSelected is null)
                Pick(hit);

            dragging = document.Selection.Count > 0 && (hit is null || document.IsSelected(hit));
            dragMoved = false;
            dragLast = inLayout;
        }
        else if (dragging && pointer.Down)
        {
            Vector2 moved = inLayout - dragLast;
            if (moved != Vector2.Zero)
            {
                foreach (var selected in document.TopSelection())
                    selected.Position += moved;

                dragLast = inLayout;
                dragMoved = true;
                codeDirty = true;
            }
        }

        if (released && dragging)
        {
            dragging = false;

            if (!dragMoved && clickedSelected is { } only)
                Select(only);
            clickedSelected = null;

            // The numbers in the inspector are from before the drag
            inspectorDirty = true;
        }
    }

    private void Quit(int failures)
    {
        System.Environment.ExitCode = failures == 0 ? 0 : 1;
        Engine.WindowManager.Window.Close();
    }
}
