using System.Numerics;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.Game;

/// <summary>
/// One of everything UIX has, a page at a time. What there is and where it sits is a layout file
/// (Assets/examples/ui/gallery.hor), this only says what each thing does, which for most of them is a line.
/// All of it works with the mouse, and all of it works without one, every module has a
/// <see cref="UINavigator"/> that walks from one control to the next by where they are on screen.
/// <list type="bullet">
/// <item>Pick, the controls that hold one value. <see cref="Dropdown"/>, <see cref="Selector"/>, a
/// <see cref="Slider"/> with ends and steps of its own, <see cref="NumberBox"/>, <see cref="ToggleButton"/>.</item>
/// <item>Colour, the <see cref="ColorPicker"/> and whoever listens to it.</item>
/// <item>Lists, a <see cref="ListBox"/>, a <see cref="ScrollPanel"/> filled from a template
/// (<see cref="UILayout.Populate(string, int)"/>) and a <see cref="GridPanel"/>.</item>
/// <item>Text, <see cref="TextBox"/>es to tab between, a label that wraps, icons in text, and an
/// <see cref="OnScreenKeyboard"/> for when there is no keyboard to type on.</item>
/// <item>Ask, what comes up over everything else, tooltips, a <see cref="UIDialog"/> and a <see cref="ContextMenu"/>.</item>
/// </list>
/// <para>
/// In your own game, the navigator is the part worth stealing.
/// <code>
/// var nav = module.Navigation;
/// nav.SelectFirst();                                  // something is selected, so the arrow keys have somewhere to start
///
/// // The arrow keys, enter and escape work by themselves. A pad is yours to hand over
/// if (pad.WasPressed(GamepadInput.DPadDown)) nav.Move(0, 1);
/// if (pad.WasPressed(GamepadInput.DPadRight)) nav.Adjust(1);      // steps a selector, slides a slider
/// if (pad.WasPressed(GamepadInput.A)) nav.Activate();
/// </code>
/// </para>
/// </summary>
public class UIControlsExample : Scene, ITestControls
{
    private const string LAYOUT = "Assets/examples/ui/gallery.hor";

    // How many rows the scroll panel gets, more than fit, which is the point of one
    private const int ROWS = 14;

    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Q / E", "previous / next page"),
        new("Arrows", "move the selection"),
        new("Enter / Space", "press what is selected"),
        new("Left / Right", "step a selector, slide a slider"),
        new("Tab", "next text box"),
        new("Esc", "close a dialog or a list first"),
    ];

    private readonly UICompositor _compositor;
    private readonly UIModule _module;
    private readonly UILayout _layout;
    private readonly TabPanel _tabs;
    private readonly Label _status;

    public UIControlsExample()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        _compositor = AddComponent(new UICompositor(camera));
        _module = _compositor.CreateModule();
        _layout = _module.LoadLayout(LAYOUT);

        _tabs = _layout.Get<TabPanel>("tabs");
        _status = _layout.Get<Label>("status");

        WirePick();
        WireColour();
        WireLists();
        WireText();
        WireAsk();

        Say("arrows and enter work on everything here, so does a mouse");
    }

    public override void PostInit()
    {
        base.PostInit();

        // Something selected from the start is what lets the arrow keys take over. Whenever the selection moves
        // the line at the bottom says where to, the way a game would play a tick sound
        var nav = _module.Navigation;
        nav.Changed = selected =>
        {
            if (selected is not null) Say($"on {Describe(selected)}");
        };
        nav.SelectFirst();

        // A page that was flipped to has nothing of it selected, the first thing on it is as good a place to start as any
        _tabs.OnChanged = _ => nav.SelectFirst();

        Engine.Graphics.ClearColor = new Vector4(0.12f, 0.13f, 0.17f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;

        // Not while somebody is typing, a Q in a name is a Q
        if (_compositor.Focus is null)
        {
            if (keyboard.WasPressed(Key.Q)) _tabs.Previous();
            if (keyboard.WasPressed(Key.E)) _tabs.Next();
        }
    }

    /// <summary>Helper method to say what the controls that hold one value do when it changes.</summary>
    private void WirePick()
    {
        // Every one of these hands over the new value, on the simulation thread
        _layout.Get<Dropdown>("fruit").OnChanged = fruit => Say($"picked {fruit}");
        _layout.Get<Selector>("difficulty").OnChanged = level => Say($"difficulty, {level.ToLowerInvariant()}");

        var bar = _layout.Get<ProgressBar>("volume_bar");
        var volume = _layout.Get<Slider>("volume");
        volume.OnChanged = value =>
        {
            // The slider goes from its Min to its Max in its own steps, the bar wants 0 to 1
            bar.Progress = volume.Progress;
            Say($"volume {value:0} of {volume.Max:0}");
        };

        _layout.Get<NumberBox>("lives").OnValueChanged = lives => Say($"{lives:0.##} lives");

        var music = _layout.Get<ToggleButton>("music");
        music.OnPressed = () => Say(music.State ? "music on" : "music off");
    }

    /// <summary>Helper method to have the swatch follow the colour picker.</summary>
    private void WireColour()
    {
        var swatch = _layout.Get<Panel>("swatch");
        _layout.Get<ColorPicker>("paint").OnChanged = colour =>
        {
            swatch.Color = colour;
            Say($"red {colour.X:0.00}, green {colour.Y:0.00}, blue {colour.Z:0.00}, {colour.W * 100.0f:0}% solid");
        };
    }

    /// <summary>Helper method to fill the scroll panel and say what the lists do.</summary>
    private void WireLists()
    {
        // Changed while it is being walked, activated when a row is confirmed, a game previews on one and commits on the other
        var stage = _layout.Get<ListBox>("stage");
        stage.OnChanged = name => Say($"looking at {name}");
        stage.OnActivated = name => Say($"{name} it is");

        // The container says which file its items are made from (template, in the layout), the game says how many
        // and fills each one in. Every item is a little layout of its own
        var rows = _layout.Populate("rows", ROWS);
        for (int i = 0; i < rows.Count; i++)
        {
            int number = i + 1;
            var row = rows[i].Get<Button>("row");
            row.Label = $"row {number} of {ROWS}";
            row.OnPressed = () => Say($"row {number}");
        }

        for (int i = 1; i <= 6; i++)
        {
            int cell = i;
            _layout.Get<Button>($"cell_{i}").OnPressed = () => Say($"cell {cell}, down goes to the one under it and not the next along");
        }
    }

    /// <summary>Helper method to put the on screen keyboard in and listen to the text boxes.</summary>
    private void WireText()
    {
        var name = _layout.Get<TextBox>("player_name");
        name.OnSubmitted = text => Say($"hello, {text}");
        _layout.Get<TextBox>("clan").OnSubmitted = text => Say($"clan {text}");

        // There is no layout word for the keyboard, so it goes into the stack the layout left for it. It types
        // into whichever box it is pointed at, for a game on a pad with no keys to type on
        _layout.Get<StackPanel>("keys").Add(new OnScreenKeyboard(OnScreenKeyboard.Alphanumeric) { Target = name });
    }

    /// <summary>Helper method for what comes up over everything else.</summary>
    private void WireAsk()
    {
        // A dialog takes the keys and the pointer until it is answered, and escape is its last button
        _layout.Get<Button>("question").OnPressed = () => UIDialog.Show(_module, "Well?",
            "A dialog sits over everything else in its module. Nothing behind it can be clicked or walked to until one of these is picked.",
            new DialogChoice("Fair enough", () => Say("fair enough")),
            new DialogChoice("Never mind", () => Say("never mind, then")));

        // A right click hands over where it happened, which is where the menu wants to come up
        var menu = _layout.Get<Button>("menu");
        menu.OnPressed = () => Say("the other button");
        menu.OnContextMenu = point =>
        {
            var options = new Menu("Right click");
            options.Add("Do a thing", () => Say("did a thing"));
            options.Add("Do another", () => Say("did another"));
            options.AddSeparator();
            options.Add("Do nothing", () => Say("nothing happened, as asked"));
            ContextMenu.Show(_module, point, options);
        };
    }

    private void Say(string text) => _status.Text = text;

    private static string Describe(UIComponent component) => component switch
    {
        Button button => $"the {button.Label} button",
        Dropdown => "the dropdown, enter opens it",
        Selector => "the selector, left and right step it",
        Slider => "the slider, left and right slide it",
        ToggleButton => "the toggle",
        NumberBox => "the number box",
        TextBox => "a text box, enter starts typing",
        ListBox => "the list, up and down walk its rows",
        ColorPicker => "the colour picker",
        _ => component.GetType().Name
    };
}
