using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

using Button = Horizon.Rendering.UIX.Components.Button;

namespace Horizon.Testing.Examples.UI;

/// <summary>
/// The bits of UIX you want for a real menu screen, all in one layout. Drag the window out to something stupid wide
/// (or tall) and watch it cope.
/// <list type="bullet">
/// <item><c>compositor.design</c>: a layout says what screen it was made for, and it's laid out on exactly that and
/// scaled to fit whatever it's actually shown on. "contain" keeps its shape smack in the middle (an ultrawide gets
/// room either side instead of a menu stretched to buggery), "stretch" lays it out over the whole screen instead.
/// F draws the frame it's made for, S flips between the two.</item>
/// <item><c>compositor.tabs</c>: pages you flip between. Every child is a page, <c>tabs</c> names them. Click a tab
/// or use Q and E (the bumpers, in a game).</item>
/// <item><c>compositor.group</c>: a bunch of things that move, hide and animate as one. The HUD in the corner is one,
/// bobbing about to prove the point.</item>
/// <item>Right click menus: give anything an <see cref="UIComponent.OnContextMenu"/> and show a
/// <see cref="ContextMenu"/> from it. Right click one of your mates.</item>
/// <item><see cref="UILayout.Bind{T}(Panel, IReadOnlyList{T}, Action{UILayout, T, int})"/>: a container filled from a
/// list that keeps changing. Only what's new gets made (and makes an entrance), the rest is kept and filled in again.
/// + and - get you more mates or fewer, blocking one gets rid of them.</item>
/// <item>The <see cref="PerformanceOverlay"/>: what the loops are up to, drawn with UIX. F3 cycles it.</item>
/// </list>
/// Hex (Horizon.Hex) does all of this with a mouse, open the layout in the code below in it if you'd rather click.
/// </summary>
public class UIScreensExample : Scene, ITestControls
{
    // The whole screen, as a layout file would have it. Normally this is a .hor in your assets, it's inline here so
    // you can read it next to the code that uses it
    private const string Layout = """
        // Made for a 16:9 screen, and kept that shape on any other
        compositor.design({ size: vec(1600, 900), fit: "contain" });

        let panel = compositor.stack({ background: "panel", padding: vec(40, 28), spacing: 16, intro: "pop", intro_time: 0.3 });
        let title = compositor.label({ parent: panel, text: "Settings, sort of", text_scale: 0.5 });

        // A page per child, named in order. The hints are just text either end of the strip
        let tabs = compositor.tabs({ parent: panel, tabs: ["Sound", "Video", "Mates"], text_scale: 0.3, spacing: 40, prev_hint: "Q", next_hint: "E" });

        let sound = compositor.stack({ parent: tabs, spacing: 10 });
        let volume = compositor.selector({ parent: sound, size: vec(440, 44), text_scale: 0.26 });
        let music = compositor.toggle({ parent: sound, label: "Music", size: vec(440, 44) });

        let video = compositor.stack({ parent: tabs, spacing: 10 });
        let quality = compositor.selector({ parent: video, size: vec(440, 44), text_scale: 0.26 });
        let shake = compositor.toggle({ parent: video, label: "Screen shake", size: vec(440, 44) });

        // Filled in from code, an item per mate out of a template (see FillMates)
        let mates = compositor.grid({ parent: tabs, columns: 3, spacing: 8, stagger: 0.05 });

        let status = compositor.label({ parent: panel, text: "", text_scale: 0.2 });

        // The HUD: a group pinned to the top left corner of the design, everything in it placed from its middle
        let hud = compositor.group({ anchor: "top_left", pos: vec(190, -60) });
        let hud_name = compositor.label({ parent: hud, pos: vec(0, 22), text: "Player 1", text_scale: 0.28 });
        let hud_health = compositor.progress_bar({ parent: hud, pos: vec(0, -10), size: vec(320, 22), progress: 0.7 });
        """;

    // What an item of the mates grid is. Normally a .hor next to the layout, written out here to keep it all in one file
    private const string MateTemplate = """
        let mate = compositor.button({ style: "button_flat", size: vec(140, 44), lbl_scale: 0.22, intro: "pop", intro_time: 0.2 });
        """;

    private static readonly string[] Everybody = ["Dave", "Shaz", "Big Tony", "Kev", "Nan", "The Cat", "Gaz", "Bev", "Him Upstairs"];

    // How far the HUD bobs, and how fast
    private const float BOB_HEIGHT = 8.0f;
    private const float BOB_SPEED = 2.0f;

    // The frame drawn round the design with F
    private static readonly Vector4 FrameColor = new(0.38f, 0.62f, 1.0f, 0.9f);

    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Q / E", "previous / next tab"),
        new("Right click", "a mate, for their menu"),
        new("F", "show what the layout is made for"),
        new("S", "keep its shape / stretch it out"),
        new("+ / -", "more mates, fewer mates"),
        new("F3", "performance overlay")
    ];

    private readonly UICompositor _compositor;
    private UIModule _module = null!;
    private TabPanel _tabs = null!;
    private Group _hud = null!;
    private Label _status = null!;
    private UILayout _layout = null!;
    private GridPanel _matesGrid = null!;
    private readonly List<string> _mates = ["Dave", "Shaz", "Big Tony", "Kev"];
    private Vector2 _hudHome;
    private float _time;

    public UIScreensExample()
    {
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;

        // No design size on the compositor: the module says what it's made for and scales itself to fit
        _compositor = AddComponent(new UICompositor(cam));

        // Off to start with, F3 brings it up. A game would keep the setting in its options (Fighter2D does)
        AddEntity(new PerformanceOverlay(PerformanceDetail.Off));
    }

    public override void PostInit()
    {
        base.PostInit();

        _module = _compositor.CreateModule();
        var layout = _layout = UILayout.LoadCode(_module, Layout);

        _tabs = layout.Get<TabPanel>("tabs");
        _hud = layout.Get<Group>("hud");
        _status = layout.Get<Label>("status");
        _hudHome = _hud.Position;

        layout.Get<Selector>("volume").Options = ["Quiet", "Normal", "Loud", "Neighbours complaining"];
        layout.Get<Selector>("quality").Options = ["Potato", "Fine", "Lovely"];

        // Somebody flipped a page, with a click or a key, it's the same event either way
        _tabs.OnChanged = page => Say($"on the {_tabs.Tabs[page]} page");

        // Templates are files, so this one gets written somewhere to be one
        string template = Path.Combine(Path.GetTempPath(), "horizon-ui-screens", "mate.hor");
        Directory.CreateDirectory(Path.GetDirectoryName(template)!);
        File.WriteAllText(template, MateTemplate);

        _matesGrid = layout.Get<GridPanel>("mates");
        _matesGrid.Template = template;
        FillMates();
        Say("right click a mate, Q and E flip the pages");

        Engine.GL.ClearColor(0.12f, 0.13f, 0.17f, 1.0f);
    }

    // Called whenever the list changes. Bind keeps the buttons it made last time, so only a new mate pops in
    private void FillMates() => _layout.Bind(_matesGrid, _mates, (item, name, _) =>
    {
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
