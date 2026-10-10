using System.Numerics;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Examples.Game;

/// <summary>
/// A whole menu screen that lives in a file, Assets/examples/ui/settings.hor, with the game only saying what
/// things do. Drag the window out to something stupid wide (or tall) and watch it cope.
/// <list type="bullet">
/// <item><c>compositor.design</c>, a layout says what screen it was made for, and it's laid out on exactly that and
/// scaled to fit whatever it's actually shown on. "contain" keeps its shape smack in the middle (an ultrawide gets
/// room either side instead of a menu stretched to buggery), "stretch" lays it out over the whole screen instead.
/// F draws the frame it's made for, S flips between the two.</item>
/// <item><c>compositor.tabs</c>, pages you flip between. Every child is a page, <c>tabs</c> names them. Click a tab
/// or use Q and E (the bumpers, in a game).</item>
/// <item><c>compositor.group</c>, a bunch of things that move, hide and animate as one. The HUD in the corner is one,
/// bobbing about to prove the point.</item>
/// <item>Right click menus, give anything an <see cref="UIComponent.OnContextMenu"/> and show a
/// <see cref="ContextMenu"/> from it. Right click one of your mates.</item>
/// <item><see cref="UILayout.Bind{T}(Panel, IReadOnlyList{T}, Action{UILayout, T, int})"/>, a container filled from a
/// list that keeps changing, an item of it out of a template file (mate.hor). Only what's new gets made (and makes
/// an entrance), the rest is kept and filled in again. + and - get you more mates or fewer, blocking one gets rid of them.</item>
/// <item>The <see cref="PerformanceOverlay"/> in the corner is a UIX layout too, the host has one on the engine for every example. F3 cycles it.</item>
/// </list>
/// Hex (Horizon.Hex) does all of this with a mouse, open settings.hor in it if you'd rather click.
/// </summary>
public class UILayoutsExample : Scene, ITestControls
{
    private const string LAYOUT = "Assets/examples/ui/settings.hor";

    private static readonly string[] Everybody = ["Dave", "Shaz", "Big Tony", "Kev", "Nan", "The Cat", "Gaz", "Bev", "Him Upstairs"];

    // How far the HUD bobs, and how fast
    private const float BOB_HEIGHT = 8.0f;
    private const float BOB_SPEED = 2.0f;

    // The frame drawn round the design with F
    private static readonly Vector4 FrameColor = new(0.38f, 0.62f, 1.0f, 0.9f);

    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Q / E", "previous / next page"),
        new("Right click", "a mate, for their menu"),
        new("F", "show what the layout is made for"),
        new("S", "keep its shape / stretch it out"),
        new("+ / -", "more mates, fewer mates")
    ];

    private readonly UIModule _module;
    private readonly UILayout _layout;
    private readonly TabPanel _tabs;
    private readonly Group _hud;
    private readonly Label _status;
    private readonly GridPanel _matesGrid;
    private readonly List<string> _mates = ["Dave", "Shaz", "Big Tony", "Kev"];
    private readonly Vector2 _hudHome;
    private float _time;

    public UILayoutsExample()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // No design size on the compositor, the layout says what it's made for and scales itself to fit
        var compositor = AddComponent(new UICompositor(camera));

        // Loading a layout runs its script. Everything it made is there to be asked for by the name the script
        // gave it, and asking for something that isn't there (or isn't what you said it is) says so loudly
        _module = compositor.CreateModule();
        _layout = _module.LoadLayout(LAYOUT);

        _tabs = _layout.Get<TabPanel>("tabs");
        _hud = _layout.Get<Group>("hud");
        _status = _layout.Get<Label>("status");
        _matesGrid = _layout.Get<GridPanel>("mates");
        _hudHome = _hud.Position;

        // Somebody flipped a page, with a click or a key, it's the same event either way
        _tabs.OnChanged = page => Say($"on the {_tabs.Tabs[page]} page");

        _layout.Get<Selector>("volume").OnChanged = value => Say($"volume, {value.ToLowerInvariant()}");
        _layout.Get<Selector>("quality").OnChanged = value => Say($"graphics, {value.ToLowerInvariant()}");

        FillMates();
        Say("right click a mate, Q and E flip the pages");
    }

    public override void PostInit()
    {
        base.PostInit();
        Engine.Graphics.ClearColor = new Vector4(0.12f, 0.13f, 0.17f, 1.0f);
    }

    // Called whenever the list changes. Bind keeps the buttons it made last time, so only a new mate pops in
    private void FillMates() => _layout.Bind(_matesGrid, _mates, (item, name, _) =>
    {
        // An item is a layout of its own, with the template's names in it and nobody else's
        var mate = item.Get<Button>("mate");
        mate.Label = name;
        mate.OnPressed = () => Say($"{name} says hi");

        // A right click gets where it happened in the layout's units, which is where the menu goes
        mate.OnContextMenu = point =>
        {
            var menu = new Menu(name);
            menu.Add("Invite", () => Say($"invited {name}"));
            menu.Add("Mute", () => Say($"muted {name}, bliss"));
            menu.AddSeparator();
            menu.Add("Block", () =>
            {
                _mates.Remove(name);
                FillMates();
                Say($"{name} is dead to you");
            });
            ContextMenu.Show(_module, point, menu);
        };
    });

    private void Say(string text) => _status.Text = text;

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        var keyboard = Engine.Input.Keyboard;

        if (keyboard.WasPressed(Key.Q))
            _tabs.Previous();
        if (keyboard.WasPressed(Key.E))
            _tabs.Next();

        if (keyboard.WasPressed(Key.Equal) || keyboard.WasPressed(Key.KeypadAdd))
        {
            if (Everybody.FirstOrDefault(name => !_mates.Contains(name)) is { } found)
            {
                _mates.Add(found);
                FillMates();
                Say($"{found} turned up");
            }
            else
                Say("that's everybody, you're not that popular");
        }

        if ((keyboard.WasPressed(Key.Minus) || keyboard.WasPressed(Key.KeypadSubtract)) && _mates.Count > 0)
        {
            Say($"{_mates[^1]} went home");
            _mates.RemoveAt(_mates.Count - 1);
            FillMates();
        }

        if (keyboard.WasPressed(Key.F))
            _module.FrameColor = _module.FrameColor is null ? FrameColor : null;

        if (keyboard.WasPressed(Key.S))
        {
            _module.Fit = _module.Fit == UIFit.Contain ? UIFit.Stretch : UIFit.Contain;
            Say(_module.Fit == UIFit.Contain ? "keeping its shape in the middle" : "stretched over the whole window");
        }

        // The group moves and everything in it goes along, nothing in it knows or cares
        _time += dt;
        _hud.Position = _hudHome + new Vector2(0, MathF.Sin(_time * BOB_SPEED) * BOB_HEIGHT);
    }
}
