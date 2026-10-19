using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Testing.Checks;

/// <summary>
/// Every skin there is under Assets/uix, in every theme it comes in, one after the other on the same panel of
/// controls, and for each of them whether it came up, whether it has the art the components ask for, and whether
/// a panel that was laid out for one skin still holds what is in it in this one. Then the setting itself
/// (<see cref="UISkins"/>), who follows it and who doesn't, stepping through it and keeping it in a file.
/// A skin somebody adds is checked the day its folder is there, nothing in here names one.
/// </summary>
internal sealed class UISkinsCheck : Scene, ISelfCheck
{
    // What the components and the layouts ask a skin with art for by name. A skin either has these or leaves them
    // to the one it falls back on, either way they have to come to something
    private static readonly string[] Wanted =
    [
        "button", "button_hover", "button_pressed", "button_flat", "button_flat_hover", "button_outline", "panel",
        "panel_window", "check_on", "check_off", "slider_knob", "bar_frame", "bar_fill", "key", "selector",
        "arrow_up", "arrow_down", "arrow_left", "arrow_right", "heart", "xbox_a", "ps_cross", "key_w", "mouse_left"
    ];

    // The names a text can draw an icon by
    private static readonly string[] Icons = ["pad_a", "pad_b", "dpad", "heart", "star", "check", "lock", "up", "mouse_left"];

    public override Camera ActiveCamera { get; protected set; }

    private readonly UICompositor _compositor, _themed, _own;
    private readonly UISelfTest _test = new();

    private StackPanel _panel = null!;
    private readonly List<UIComponent> _controls = [];
    private Button _button = null!;
    private Label _name = null!;

    public bool Finished => _test.Finished;
    public int Failed => _test.Failed;
    public int Count => _test.Count;

    public UISkinsCheck()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // One that follows the game's setting, one that follows it but would like a theme of its own, and one in
        // a skin of its own that the setting is none of its business
        _compositor = AddComponent(new UICompositor(camera));
        _themed = AddComponent(new UICompositor(camera, "red"));
        _own = AddComponent(new UICompositor(camera, "Assets/uix/flat/", "skin.hor"));

        _compositor.PointerSource = () => _test.Pointer;
    }

    public override void PostInit()
    {
        base.PostInit();
        Engine.Graphics.ClearColor = new Vector4(0.12f, 0.13f, 0.17f, 1.0f);

        Build();
        Script(_test);
    }

    /// <summary>Helper method to put one of every control a menu is made of on a panel, the way a game would.</summary>
    private void Build()
    {
        _panel = _compositor.CreateModule().AddComponent(new StackPanel { Background = "panel", Padding = new UIEdges(36, 30), Spacing = 14 });

        T Add<T>(T control) where T : UIComponent
        {
            _controls.Add(_panel.Add(control));
            return control;
        }

        _name = Add(new Label("a skin"));
        Add(new Label("[icon:pad_a] jump   [icon:key_w] walk   [icon:heart] lives") { TextScale = 0.3f });
        Add(new Label("[icon:up][icon:down][icon:left][icon:right] move   [icon:stick_up] aim   [icon:mouse_left] fire") { TextScale = 0.3f });
        _button = Add(new Button("A button"));
        Add(new Button("A quiet one") { Style = "button_flat" });
        Add(new ToggleButton("A box to tick", true));
        Add(new Slider { Size = new Vector2(420, 0), Value = 0.4f });
        Add(new ProgressBar { Size = new Vector2(420, 0), Progress = 0.6f });
        Add(new TextBox("something typed") { Size = new Vector2(420, 0) });
        Add(new Selector("one", "two", "three") { Size = new Vector2(420, 0) });
        Add(new Dropdown("first", "second") { Size = new Vector2(420, 0) });

        // Something on each of the other two, so they have a skin to load
        _themed.CreateModule().AddComponent(new Label("themed") { Anchor = Origin.BottomLeft, Position = new Vector2(30, 30) });
        _own.CreateModule().AddComponent(new Label("own skin") { Anchor = Origin.BottomRight, Position = new Vector2(-30, 30) });
    }

    private void Script(UISelfTest test)
    {
        var skins = UISkins.Available;

        test.Wait();
        test.Check("skins: there is more than the one games always had, and the setting starts on that one", () =>
            skins.Count >= 5 && UISkins.Current.Name == UISkins.DEFAULT && UISkins.Find("no_such_skin") is null);
        test.Check("skins: every one of them says what it is called and which theme is its usual one", () =>
            skins.All(skin => skin.Title.Length > 0 && File.Exists(skin.Directory + skin.File) && (skin.Themes.Count == 0 || skin.Has(skin.UsualTheme))));
        test.Check("skins: a UI made without a skin follows the setting, one made with its own doesn't", () =>
            _compositor.FollowsSkins && _themed.FollowsSkins && !_own.FollowsSkins);

        foreach (UISkinChoice skin in skins)
        {
            foreach (string? theme in skin.Themes.Count > 0 ? skin.Themes.Cast<string?>() : [null])
            {
                string what = theme is null ? skin.Name : $"{skin.Name} in {theme}";

                test.Run(() =>
                {
                    UISkins.Use(skin, theme);
                    _name.Text = theme is null ? skin.Title : $"{skin.Title}, {theme}";
                });
                test.WaitFor(() => _compositor.Skin is { } worn && Matches(worn, skin, theme) && !worn.HasPendingArt, 4.0f);

                // Asking is what has a skin stitch its art in, so everything is asked for and then given a moment
                test.Run(() =>
                {
                    if (_compositor.Skin is not { Sheet: not null } worn) return;
                    foreach (string name in Wanted) worn.TryGetRegion(name, out _);
                    foreach (string icon in Icons) worn.TryGetIcon(icon, out _);
                });
                test.WaitFor(() => _compositor.Skin is { HasPendingArt: false }, 4.0f);
                test.Wait();

                test.Check($"{what}: comes up, and the UI that follows the setting is in it", () =>
                    UISkins.Current == skin && _compositor.Skin is { } worn && Matches(worn, skin, theme));

                test.Check($"{what}: has art for everything the components ask for by name, or colours instead", () =>
                    _compositor.Skin is { } worn && (worn.Sheet is null || Missing(worn).Length == 0));

                // An icon says how many pixels of its art make a line. A pack that brings bigger art for one and
                // leaves that number as it was has it drawn over the lines above and below, which is what the
                // arrows of an input display did in the first three packs that had arrows of their own
                test.Check($"{what}: no icon in a line of text is taller than the line by much", () =>
                    _compositor.Skin is { } worn && TooTall(worn).Length == 0);

                test.Check($"{what}: the panel holds what is in it and nothing is squashed to nothing", () =>
                    _controls.All(control => control.Bounds.Width > 8 && control.Bounds.Height > 8 && Inside(_panel.Bounds, control.Bounds))
                    && _panel.Bounds.Width < Engine.WindowManager.ViewportSize.X);

                test.Check($"{what}: what is written can be told from what it is written on", () =>
                    _compositor.Skin is { } worn && Contrast(worn.TextColor, worn.PanelColor) > 0.25f);

                // A UI that wanted red gets red where the skin has one, and the game's theme where it hasn't
                test.Check($"{what}: a UI that asked for a theme gets it only if the skin has one by that name", () =>
                    _themed.Skin is { } worn && worn.Theme == (skin.Has("red") ? "red" : theme ?? worn.Theme));

                test.Click(() => _button.Module!.ToWorld(_button.Bounds.Center));
                test.Check($"{what}: a button is still where it is drawn", () => _presses > 0 && (_presses = 0) == 0);
            }
        }

        test.Check("skins: the UI in a skin of its own never left it", () => _own.Skin is { Sheet: null });

        /* The setting itself */

        test.Run(() => UISkins.Use(UISkins.DEFAULT));
        test.WaitFor(() => _compositor.Skin is { } worn && Matches(worn, UISkins.Current, null), 3.0f);
        test.Check("setting: back to the one games come in", () => UISkins.Current.Name == UISkins.DEFAULT && UISkins.Theme is null);

        int before = 0;
        test.Run(() => before = UISkins.Version);
        test.Run(() => UISkins.Use(UISkins.DEFAULT));
        test.Check("setting: asking for what it is already changes nothing", () => UISkins.Version == before);
        test.Check("setting: a skin nobody has is turned down", () => !UISkins.Use("no_such_skin") && UISkins.Version == before);
        test.Run(() => UISkins.Use(UISkins.DEFAULT, "no_such_theme"));
        test.Check("setting: a theme the skin hasn't got is its usual one", () => UISkins.Theme is null);

        // Round every skin and back to where it started
        string walked = string.Empty;
        test.Run(() =>
        {
            for (int i = 0; i < skins.Count; i++)
                walked += UISkins.Step(1).Name + " ";
        });
        test.Check("setting: stepping goes round every skin there is and ends where it began", () =>
            UISkins.Current.Name == UISkins.DEFAULT && skins.All(skin => walked.Contains(skin.Name + " ")));

        string? firstTheme = null;
        test.Run(() => firstTheme = UISkins.StepTheme(1));
        test.Check("setting: and through the themes of the one that is up", () =>
            firstTheme is not null && UISkins.Current.Has(firstTheme) && UISkins.Theme == firstTheme);

        // Kept in a file, and read back out of it
        string kept = Path.Combine(Path.GetTempPath(), $"horizon_skin_check_{Environment.ProcessId}.txt");
        test.Run(() =>
        {
            File.Delete(kept);
            UISkins.Remember(kept);
            UISkins.Use("retro_windows", "desert");
        });
        test.Check("setting: with a file to keep it in, a change is written to it", () =>
            File.Exists(kept) && File.ReadAllText(kept) == "retro_windows:desert");
        test.Run(() =>
        {
            UISkins.Remember(null);
            UISkins.Use(UISkins.DEFAULT);
        });
        test.Check("setting: told to stop it leaves the file alone", () => File.ReadAllText(kept) == "retro_windows:desert");
        test.Run(() => UISkins.Remember(kept));
        test.Check("setting: and the file is what it comes back up in", () =>
            UISkins.Current.Name == "retro_windows" && UISkins.Theme == "desert");

        // A UI can leave the setting and come back to it
        test.Run(() => _compositor.SetSkin("Assets/uix/flat/", "skin.hor"));
        test.WaitFor(() => _compositor.Skin is { Sheet: null }, 3.0f);
        test.Check("setting: a UI told to wear a skin of its own stops following", () => !_compositor.FollowsSkins && _compositor.Skin is { Sheet: null });
        test.Run(() => _compositor.FollowSkins());
        test.WaitFor(() => _compositor.Skin is { Sheet: not null }, 3.0f);
        test.Check("setting: and follows again when told to", () => _compositor.FollowsSkins && _compositor.Skin is { Theme: "desert" });

        test.Run(() =>
        {
            UISkins.Remember(null);
            UISkins.Use(UISkins.DEFAULT);
            File.Delete(kept);
            _name.Text = "done";
        });
        test.WaitFor(() => _compositor.Skin is { } worn && Matches(worn, UISkins.Current, null), 3.0f);
    }

    private int _presses;

    /// <summary>Whether a skin that is loaded is the one of a choice, in a theme (null for whichever is its usual one).</summary>
    private static bool Matches(UISkin worn, UISkinChoice skin, string? theme)
    {
        bool sameFile = worn.Sheet is null
            ? skin.Themes.Count == 0 || worn.Theme is null || skin.Has(worn.Theme)
            : Path.GetFullPath(worn.Sheet.Path).StartsWith(Path.GetFullPath(skin.Directory), StringComparison.OrdinalIgnoreCase);

        return sameFile && worn.Theme == (theme ?? skin.UsualTheme);
    }

    /// <summary>The names a skin was asked for and has nothing for.</summary>
    private static string[] Missing(UISkin worn)
    {
        string[] missing = [.. Wanted.Where(name => !worn.TryGetRegion(name, out _)), .. Icons.Where(icon => !worn.TryGetIcon(icon, out _)).Select(icon => "icon " + icon)];
        if (missing.Length > 0)
            Console.WriteLine($"[UI self-test] nothing for {string.Join(", ", missing)}");

        return missing;
    }

    // The icons whose art isn't simply drawn as tall as the line, and how tall is too tall, in lines
    private static readonly string[] Sized = ["up", "down", "left", "right", "stick_up", "stick_left", "mouse_left", "mouse_right", "pad_a", "heart", "dpad"];
    private const float TALLEST = 1.35f;

    /// <summary>The icons of a skin that come out taller than a line of text has room for.</summary>
    private static string[] TooTall(UISkin worn)
    {
        var icons = (Horizon.UI.Drawing.IUIIconSource)worn;
        var tall = new List<string>();

        foreach (string name in Sized)
        {
            if (icons.TryGetIconSize(name, string.Empty, out Vector2 texels, out float line) && line > 0.0f && texels.Y / line > TALLEST)
                tall.Add($"{name} ({texels.Y / line:0.0} lines)");
        }

        if (tall.Count > 0)
            Console.WriteLine($"[UI self-test] too tall for a line: {string.Join(", ", tall)}");

        return [.. tall];
    }

    private static bool Inside(UIRect outer, UIRect inner) =>
        inner.Min.X >= outer.Min.X - 1 && inner.Max.X <= outer.Max.X + 1 && inner.Min.Y >= outer.Min.Y - 1 && inner.Max.Y <= outer.Max.Y + 1;

    /// <summary>How far apart two colours are in how bright they are, 0 for the same and 1 for black on white.</summary>
    private static float Contrast(Vector4 a, Vector4 b)
    {
        static float Light(Vector4 c) => 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z;
        return MathF.Abs(Light(a) - Light(b));
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        // The button counts its presses, wired here so the script can ask
        _button.OnPressed ??= () => _presses++;
        _test.Update(dt);
    }
}
