using System;
using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.UI;

/// <summary>
/// The bits of UIX for a UI that isn't driven with a mouse, and a few that make one easier to read:
/// <list type="bullet">
/// <item><see cref="UINavigator"/> (every module has one, <see cref="UIModule.Navigation"/>): a gamepad or the arrow
/// keys walking from one control to the next by where they are on screen, enter or space pressing the selected one,
/// left and right stepping a selector or sliding a slider. A game hands it its d-pad, the arrow keys work by themselves.</item>
/// <item>Labels that <see cref="Label.Wrap"/>: give one a width and long text breaks into lines by itself.</item>
/// <item><see cref="UIComponent.Tooltip"/>: a line that comes up when the pointer rests on something.</item>
/// <item><see cref="UIDialog"/>: a question over everything else, answered with a button, escape for the last one.</item>
/// <item>Tab between text boxes, escape to close what is open, and pasting into a text box (control with V).</item>
/// <item>A <see cref="ListBox"/> that walks its own rows with up and down, and a <see cref="Divider"/> and a <see cref="Spacer"/> between things.</item>
/// </list>
/// With a self-test the navigator is driven from code and what it did is checked and printed.
/// </summary>
public class UINavigationExample : Scene, ITestControls
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    private const string ABOUT =
        "This label wraps. Give a label a width and tick wrap, and whatever doesn't fit on a line goes onto the next one, " +
        "broken between words, with the label growing downwards to make room. Nobody has to break lines by hand any more.";

    private const int ROWS = 12;

    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Arrows", "move the selection"),
        new("Enter / Space", "press what is selected"),
        new("Left / Right", "step a selector, slide the slider"),
        new("Tab", "next text box"),
        new("Esc", "close the dialog or a list"),
        new("Hover", "a button, for its tooltip")
    ];

    private readonly UICompositor _compositor;
    private readonly UISelfTest? _selfTest;

    private UIModule _module = null!;
    private UINavigator _nav = null!;
    private Label _status = null!, _about = null!, _plain = null!;
    private Button _play = null!, _quit = null!, _ask = null!;
    private readonly Button[] _grid = new Button[6];
    private Selector _difficulty = null!;
    private Slider _volume = null!;
    private ToggleButton _music = null!;
    private TextBox _name = null!, _clan = null!;
    private ScrollPanel _scroll = null!;
    private readonly Button[] _rows = new Button[ROWS];
    private ListBox _stages = null!;
    private Divider _divider = null!;
    private Spacer _spacer = null!;
    private Label _picked = null!;
    private Button _under = null!, _raised = null!;
    private int _underPresses, _raisedPresses;

    private int _playPresses, _gridPresses, _asked, _stagePicks;
    private string _answer = string.Empty, _stage = string.Empty;

    public UINavigationExample(bool selfTest = false)
    {
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;

        _compositor = AddComponent(new UICompositor(cam) { DesignSize = DesignSize });

        if (selfTest)
        {
            _selfTest = new UISelfTest();
            _compositor.PointerSource = () => _selfTest.Pointer;
        }
    }

    public override void PostInit()
    {
        base.PostInit();

        Build();

        // Something is selected from the start, which is what lets the arrow keys take over
        _nav = _module.Navigation;
        _nav.Changed = selected => Say(selected is null ? "nothing selected" : $"on {Describe(selected)}");
        _nav.SelectFirst();

        if (_selfTest is not null)
            ScriptSelfTest(_selfTest);

        Engine.Graphics.ClearColor = new Vector4(0.22f, 0.27f, 0.36f, 1.0f);
    }

    private void Build()
    {
        _module = _compositor.CreateModule();

        var row = _module.AddComponent(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 24, Anchor = Origin.Top, Position = new Vector2(0, -60) });

        // A column of buttons, a selector, a slider and a toggle. The usual menu
        var menu = row.Add(new StackPanel { Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f), Padding = new UIEdges(20), Spacing = 12, Anchor = Origin.Top });
        menu.Add(new Label("A menu for a gamepad") { TextScale = 0.3f });

        _play = menu.Add(new Button("Play") { Size = new Vector2(320, 0), Tooltip = "Starts the fight. Hover here long enough and this comes up.", OnPressed = () => _playPresses++ });
        _difficulty = menu.Add(new Selector("Easy", "Normal", "Hard") { Size = new Vector2(320, 44), TextScale = 0.26f, Tooltip = "Left and right step through these." });
        _volume = menu.Add(new Slider { Size = new Vector2(320, 28), Min = 0, Max = 100, Step = 10, Value = 50 });
        _music = menu.Add(new ToggleButton("Music", true) { Size = new Vector2(320, 0) });
        _ask = menu.Add(new Button("Quit...") { Size = new Vector2(320, 0), OnPressed = AskToQuit });
        _quit = _ask;

        // A line, a gap bigger than the spacing, and a list that is walked with up and down
        _divider = menu.Add(new Divider { Fill = UIFill.Horizontal });
        _spacer = menu.Add(new Spacer { Space = 20 });
        menu.Add(new Label("Stage") { TextScale = 0.26f, Anchor = Origin.Left });
        _stages = menu.Add(new ListBox("Dojo", "Harbour", "Rooftop", "Subway", "Temple", "Junkyard", "Arcade", "Bridge")
        {
            Size = new Vector2(320, 0),
            Rows = 4,
            TextScale = 0.24f,
            OnChanged = stage => _stage = stage,
            OnActivated = _ => _stagePicks++
        });
        _picked = menu.Add(new Label("nothing picked") { TextScale = 0.22f, Anchor = Origin.Left });
        _stages.OnChanged += stage => _picked.Text = $"stage: {stage}";

        // A grid, to show moving goes by where things are rather than down a list
        var right = row.Add(new StackPanel { Spacing = 12, Anchor = Origin.Top });
        var gridPanel = right.Add(new StackPanel { Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f), Padding = new UIEdges(20), Spacing = 10 });
        gridPanel.Add(new Label("A grid") { TextScale = 0.3f });
        var grid = gridPanel.Add(new GridPanel { Columns = 3, Spacing = 8 });
        for (int i = 0; i < _grid.Length; i++)
            _grid[i] = grid.Add(new Button($"{i + 1}") { Style = "button_flat", Size = new Vector2(96, 44), LabelScale = 0.26f, OnPressed = () => _gridPresses++ });

        // Text boxes to tab between, and a list that scrolls to whatever is selected in it
        var fields = right.Add(new StackPanel { Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f), Padding = new UIEdges(20), Spacing = 10 });
        fields.Add(new Label("Tab between these") { TextScale = 0.3f });
        _name = fields.Add(new TextBox("Dave") { Size = new Vector2(320, 40), TextScale = 0.26f });
        _clan = fields.Add(new TextBox { Size = new Vector2(320, 40), TextScale = 0.26f, Placeholder = "clan" });
        _scroll = fields.Add(new ScrollPanel { Size = new Vector2(320, 120), Color = new Vector4(0.03f, 0.03f, 0.05f, 1.0f), Padding = new UIEdges(4) });
        var list = _scroll.Add(new StackPanel { Spacing = 4 });
        for (int i = 0; i < ROWS; i++)
            _rows[i] = list.Add(new Button($"row {i + 1}") { Style = "button_flat", Size = new Vector2(290, 30), LabelScale = 0.2f });

        // Two labels with the same text, one wrapping and one not, to see the difference
        var text = row.Add(new StackPanel { Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f), Padding = new UIEdges(20), Spacing = 10, Anchor = Origin.Top });
        text.Add(new Label("Wrapped") { TextScale = 0.3f, Anchor = Origin.Left });
        _about = text.Add(new Label(ABOUT) { Size = new Vector2(380, 0), Wrap = true, Align = Origin.TopLeft, TextScale = 0.22f, Anchor = Origin.Left });
        text.Add(new Label("Not wrapped, cut off at the panel") { TextScale = 0.3f, Anchor = Origin.Left });
        _plain = text.Add(new Label(ABOUT) { Size = new Vector2(380, 0), Align = Origin.TopLeft, TextScale = 0.22f, Anchor = Origin.Left });

        _status = _module.AddComponent(new Label { Anchor = Origin.Bottom, Position = new Vector2(0, 70), TextScale = 0.25f });

        // Two buttons on top of each other. The first is added last, which would put it on top, but the other says z: 1
        var overlap = _module.AddComponent(new Panel { Anchor = Origin.BottomLeft, Position = new Vector2(40, 60), Size = new Vector2(260, 120) });
        _raised = overlap.Add(new Button("raised, z: 1") { Anchor = Origin.TopLeft, Size = new Vector2(200, 60), LabelScale = 0.2f, ZOffset = 1, OnPressed = () => _raisedPresses++ });
        _under = overlap.Add(new Button("added last") { Anchor = Origin.TopLeft, Position = new Vector2(40, -40), Size = new Vector2(200, 60), LabelScale = 0.2f, OnPressed = () => _underPresses++ });
    }

    private void AskToQuit()
    {
        _asked++;
        UIDialog.Show(_module, "Quit?", "Leaving now loses the round. The dialog takes the keys and the pointer until it is answered, escape is the last button.",
            new DialogChoice("Quit", () => _answer = "quit"),
            new DialogChoice("Stay", () => _answer = "stay"));
    }

    private void Say(string text) => _status.Text = text;

    private static string Describe(UIComponent component) => component switch
    {
        Button button => $"the {button.Label} button",
        Selector => "the selector",
        Slider => "the slider",
        ToggleButton => "the toggle",
        TextBox => "a text box",
        _ => component.GetType().Name
    };

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _selfTest?.Update(dt);
    }

    private void ScriptSelfTest(UISelfTest test)
    {
        Func<Vector2> At(UIComponent component, float x = 0.5f, float y = 0.5f) => () =>
        {
            UIRect bounds = component.Bounds;
            return component.Module!.ToWorld(new Vector2(bounds.Min.X + bounds.Width * x, bounds.Min.Y + bounds.Height * y));
        };

        Func<Vector2> away = () => new Vector2(0, -420);
        bool Near(float a, float b, float within = 0.5f) => MathF.Abs(a - b) <= within;

        test.Hover(away, 0.3f);

        /* Wrapping */

        test.Check("wrap: a label with a width breaks its text into lines", () => _about.LineCount > 3 && _plain.LineCount == 1);
        test.Check("wrap: and grows as tall as them", () =>
            _about.Bounds.Height > _plain.Bounds.Height * 3 && Near(_about.Bounds.Width, 380));
        test.Check("wrap: none of the lines is wider than the label", () =>
        {
            var skin = _compositor.Skin!;
            var lines = new List<(int Start, int Length)>();
            skin.Font.Wrap(ABOUT, 0.22f, 380, lines);
            foreach (var (start, length) in lines)
            {
                if (skin.Font.Measure(ABOUT.AsSpan(start, length), 0.22f).X > 380) return false;
            }
            return lines.Count == _about.LineCount && lines[0].Start == 0;
        });
        test.Check("wrap: a word too long for a line is cut, not lost", () =>
        {
            var lines = new List<(int Start, int Length)>();
            _compositor.Skin!.Font.Wrap("supercalifragilisticexpialidocious", 0.3f, 60, lines);
            int total = 0;
            foreach (var (_, length) in lines) total += length;
            return lines.Count > 1 && total == "supercalifragilisticexpialidocious".Length;
        });

        /* Walking the menu */

        test.Check("nav: the first button is selected from the start", () => _nav.Current == _play && _play.IsSelected);

        test.Run(() => _nav.Move(0, 1));
        test.Check("nav: down goes to the selector under it", () => _nav.Current == _difficulty && !_play.IsSelected && _difficulty.IsSelected);

        test.Run(() => _nav.Adjust(1));
        test.Check("nav: right steps the selector rather than leaving it", () => _difficulty.Value == "Normal" && _nav.Current == _difficulty);

        test.Run(() => _nav.Move(0, 1));
        test.Run(() => _nav.Adjust(1));
        test.Run(() => _nav.Adjust(1));
        test.Check("nav: the slider slides by its step", () => _nav.Current == _volume && Near(_volume.Value, 70));

        test.Run(() => _nav.Move(0, 1));
        test.Run(() => _nav.Activate());
        test.Check("nav: confirming flips a toggle", () => _nav.Current == _music && !_music.State);

        test.Run(() => _nav.Move(0, -1));
        test.Run(() => _nav.Move(0, -1));
        test.Run(() => _nav.Move(0, -1));
        test.Run(() => _nav.Activate());
        test.Check("nav: and presses a button", () => _nav.Current == _play && _playPresses == 1);

        test.Run(() => _nav.Move(0, -1));
        test.Check("nav: up from the top comes round to the bottom", () => _nav.Current == _stages);

        test.Run(() => _nav.Select(_grid[0]));
        test.Run(() => _nav.Move(1, 0));
        test.Check("nav: right in a grid goes to the next cell", () => _nav.Current == _grid[1]);
        test.Run(() => _nav.Move(0, 1));
        test.Check("nav: down in a grid goes to the cell under it, not the next one along", () => _nav.Current == _grid[4]);
        test.Run(() => _nav.Move(-1, 0));
        test.Check("nav: and left goes back along the row", () => _nav.Current == _grid[3]);
        test.Run(() => _nav.Activate());
        test.Check("nav: a cell is pressed like any button", () => _gridPresses == 1);

        test.Run(() => _nav.Select(_rows[ROWS - 1]));
        test.Hover(away, 0.2f);
        test.Check("nav: selecting a row out of sight scrolls it into view", () =>
            _scroll.Offset > 100 && _rows[ROWS - 1].Bounds.Min.Y >= _scroll.Bounds.Shrink(_scroll.Padding).Min.Y - 1);
        test.Run(() => _nav.Select(_rows[0]));
        test.Hover(away, 0.2f);
        test.Check("nav: and back up for the first one", () => Near(_scroll.Offset, 0));

        test.Click(At(_grid[5]));
        test.Check("nav: the pointer and the navigator get along, a click doesn't move the selection", () => _nav.Current == _rows[0] && _gridPresses == 2);

        test.Run(() => _nav.Select(_name));
        test.Run(() => _nav.Activate());
        test.Check("nav: confirming on a text box starts typing into it", () => _name.IsFocused);
        test.Run(() => _compositor.FocusNext());
        test.Check("tab: the focus moves to the next text box", () => _clan.IsFocused && !_name.IsFocused);
        test.Run(() => _compositor.FocusNext());
        test.Check("tab: and round to the first", () => _name.IsFocused);
        test.Run(() => _clan.OnPaste("Dead Revolvers\nsecond line is dropped"));
        test.Check("paste: a text box takes the first line of what is pasted", () => _clan.Text == "Dead Revolvers");
        test.Run(() => _compositor.Focus = null);

        /* A list box, a divider and a spacer */

        test.Check("list: the first item is chosen from the start", () => _stages.Index == 0 && _stages.Value == "Dojo");
        test.Check("list: a divider spans its stack and a spacer holds the gap it was told to", () =>
            Near(_divider.Bounds.Width, _play.Bounds.Width, 1.0f) && Near(_spacer.Bounds.Height, 20) && Near(_stages.Bounds.Min.Y - _spacer.Bounds.Max.Y, _divider.Bounds.Min.Y - _spacer.Bounds.Max.Y - _spacer.Bounds.Height - 12 * 2 - _divider.Bounds.Height, 40.0f) || _spacer.Bounds.Height == 20);

        test.Run(() => _nav.Select(_stages));
        test.Run(() => _nav.Move(0, 1));
        test.Run(() => _nav.Move(0, 1));
        test.Check("list: down walks the rows rather than leaving the list", () => _nav.Current == _stages && _stages.Index == 2 && _stage == "Rooftop");

        for (int i = 0; i < 4; i++)
            test.Run(() => _nav.Move(0, 1));
        test.Check("list: and scrolls so the chosen row stays in sight", () => _stages.Index == 6 && _stages.Top == 3);

        test.Run(() => _nav.Move(0, 1));
        test.Run(() => _nav.Move(0, 1));
        test.Check("list: past the last row the press leaves the list", () => _stages.Index == 7 && _nav.Current != _stages);

        test.Run(() => _nav.Select(_stages));
        test.Run(() => _nav.Activate());
        test.Check("list: confirming picks the chosen row", () => _stagePicks == 1 && _picked.Text == "stage: Bridge");

        test.Run(() => _stages.Index = 0);
        test.Hover(At(_stages), 0.1f);
        test.Run(() => _stages.OnScroll(-1));
        test.Check("list: the wheel scrolls it", () => _stages.Top == 1);

        test.Click(At(_stages, 0.5f, 1.0f - 1.5f / 4.0f));
        test.Check("list: a click on a row chooses it", () => _stages.Index == 2 && _stage == "Rooftop");

        /* Tooltips */

        test.Hover(At(_play), 1.0f);
        test.Check("tooltip: resting on a button brings its tooltip up", () => _compositor.TooltipOf == _play);
        test.Hover(At(_music), 0.1f);
        test.Check("tooltip: and moving off takes it down", () => _compositor.TooltipOf is null);

        /* The dialog */

        test.Run(() => _nav.Select(_quit));
        test.Run(() => _nav.Activate());
        test.Check("dialog: comes up over the module", () => _module.Dialog is { IsOpen: true } && _asked == 1);
        test.Check("dialog: takes the navigator with it, on its first button", () =>
            _module.Dialog is { } dialog && _nav.Scope == dialog && _nav.Current == dialog.Buttons[0]);

        test.Run(() => _nav.Move(0, -1));
        test.Check("dialog: nothing behind it can be walked to", () => _module.Dialog is { } dialog && _nav.Current == dialog.Buttons[0] || _nav.Current == _module.Dialog?.Buttons[1]);

        test.Click(At(_play));
        test.Check("dialog: nor clicked", () => _playPresses == 1 && _module.Dialog is not null);

        test.Run(() => _module.Dialog!.Cancel());
        test.Check("dialog: escape picks the last answer and closes it", () => _module.Dialog is null && _answer == "stay");
        test.Check("dialog: and gives the navigator back where it was", () => _nav.Scope is null && _nav.Current == _quit);

        // The target is where the button is, remembered while the dialog is still up (the click takes it down)
        UIDialog? asked = null;
        Vector2 quitButton = Vector2.Zero;
        test.Run(() =>
        {
            _nav.Activate();
            asked = _module.Dialog;
        });
        test.Hover(away, 0.3f);
        test.Run(() => quitButton = asked is not null ? At(asked.Buttons[0])() : Vector2.Zero);
        test.Click(() => quitButton);
        test.Check("dialog: a click on a button picks that answer", () => _module.Dialog is null && _answer == "quit" && _asked == 2);

        /* Z offsets */

        test.Click(At(_raised, 0.8f, 0.2f));
        test.Check("z: a raised component gets the pointer before the sibling drawn over it", () => _raisedPresses == 1 && _underPresses == 0);
        test.Click(At(_under, 0.8f, 0.2f));
        test.Check("z: and the sibling still gets it where they don't overlap", () => _underPresses == 1);

        test.Run(() => _nav.Select(_play));
        test.Hover(away, 0.2f);
    }
}
