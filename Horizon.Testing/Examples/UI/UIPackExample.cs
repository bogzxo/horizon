using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.Core.Tweening;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

using Button = Horizon.Rendering.UIX.Components.Button;

namespace Horizon.Testing.Examples.UI;

/// <summary>
/// Tests the new Dead Revolver UI pack as a skin. Everything on screen is cut out of the pack's sheets by the names
/// in Assets/uix/dead_revolver/sprites.hor and stitched into one atlas, which only ever holds the pieces
/// this scene shows. The same controls are drawn in every colour the pack comes in, one skin file each,
/// and the text has icons in it: sprites of the pack, and gamepad buttons made out of a blank one.
/// What drifts about behind the panel isn't UI at all: those are <see cref="Sprite"/>s in a
/// <see cref="SpriteBatch"/>, showing sprites of the same pack out of an atlas of their own.
/// </summary>
public class UIPackExample : Scene, ITestControls
{
    private const string SkinDirectory = "Assets/uix/dead_revolver/";
    private const int DrifterCount = 36;
    private const float DrifterScale = 3.0f;

    private static readonly string[] Themes = ["blue", "purple", "red", "gold", "tan", "white"];

    // What drifts about behind the panel. The last few come in the colour of the theme.
    private static readonly string[] Drifters =
        ["heart", "star", "heart_gem_blue", "star_purple", "click_star", "cursor_wait", "icon_sword", "icon_shield", "slot_filled"];

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host.
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("T", "next theme"),
        new("Mouse", "press, drag and type"),
    ];

    private readonly UICompositor compositor;
    private readonly StackPanel panel;
    private readonly Label themeName, atlasSize;
    private readonly ProgressBar progress;
    private int theme;

    private readonly SpriteSheetDefinition sprites;
    private readonly TextureAtlas spriteAtlas = new(512, 512);
    private readonly List<(Sprite Sprite, string Name, Vector2 Velocity, float Spin)> drifters = [];

    public UIPackExample()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;

        // Sprites take their art from the same definition the skin does, by the same names.
        sprites = SpriteSheetDefinition.Load(SkinDirectory, "sprites.hor");
        CreateDrifters(AddEntity(new SpriteBatch()));

        // Components need nothing from the GPU, so the whole screen can be put together right here.
        compositor = AddComponent(new UICompositor(camera, Themes[theme]));

        panel = compositor.CreateModule().AddComponent(new StackPanel
        {
            Background = "panel",
            Padding = new UIEdges(40, 34),
            Spacing = 20
        });

        var heading = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 16 });
        heading.Add(new Image("star") { Size = new Vector2(54, 45) });
        var name = heading.Add(new Label("Dead Revolver") { TextScale = 0.6f });
        heading.Add(new Image("star") { Size = new Vector2(54, 45) });

        themeName = panel.Add(new Label { TextScale = 0.3f });

        // The same prompt three ways. The icons are the art of the pack, the buttons of a gamepad blink a press now and then
        const string prompt = "[icon:pad_a] jump   [icon:pad_x] attack   [icon:pad_rt] aim   [icon:dpad_left][icon:dpad_right] move   [icon:pad_menu] pause";
        panel.Add(new Label(prompt) { TextScale = 0.28f });
        panel.Add(new Label("[icons:playstation]" + prompt) { TextScale = 0.28f });
        panel.Add(new Label("[icons:keyboard]" + prompt) { TextScale = 0.28f });

        // Buttons take their three states from the pack, the last one is drawn with different art altogether.
        var buttons = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 14 });
        buttons.Add(new Button("Play") { Size = new Vector2(150, 0), OnPressed = () => progress!.Progress += 0.1f });
        buttons.Add(new Button("Options") { Size = new Vector2(190, 0), OnPressed = () => progress!.Progress -= 0.1f });
        buttons.Add(new Button("Locked") { Size = new Vector2(170, 0), Enabled = false });
        buttons.Add(new Button("Quit") { Style = "button_flat", Size = new Vector2(120, 54) });

        progress = panel.Add(new ProgressBar { Size = new Vector2(540, 36), Progress = 0.6f, TextScale = 0.3f });
        var slider = panel.Add(new Slider { Size = new Vector2(540, 36), Value = 0.6f });
        slider.OnChanged = value => progress.Progress = value;

        var form = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 28 });
        form.Add(new ToggleButton("Music", startingState: true) { LabelScale = 0.3f });
        form.Add(new ToggleButton("Rumble") { LabelScale = 0.3f });
        form.Add(new TextBox("Player one") { Size = new Vector2(280, 54), TextScale = 0.3f, MaxLength = 14 });

        // Icons in a line of text: gamepad buttons the skin makes out of a blank sprite, and sprites by name.
        panel.Add(new Label("[icon:pad_a] confirm   [icon:pad_b] back   [icon:pad_x] [icon:pad_y] attack   [icon:dpad] move   [icon:pad_rb] block")
        {
            TextScale = 0.3f
        });
        panel.Add(new Label("[icon:ps_cross] confirm   [icon:ps_circle] back   [icon:ps_square] [icon:ps_triangle] attack   [icon:dpad] move   [icon:ps_r1] block")
        {
            TextScale = 0.3f
        });
        panel.Add(new Label("Lives [icon:heart][icon:heart][icon:heart_empty]   Rank [icon:star][icon:star][icon:star_empty]   [icon:lock] locked   [icon:sword] 12   [icon:shield] 7")
        {
            TextScale = 0.3f
        });

        // Sprites with frames play by themselves.
        var animated = panel.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 24 });
        foreach (string sprite in (string[])["cursor_wait", "click_star", "click_ripple", "click_square", "star_blue", "star_red"])
            animated.Add(new Image(sprite) { Size = new Vector2(48, 48) });

        atlasSize = panel.Add(new Label { TextScale = 0.25f, Color = new Vector4(0.93f, 0.95f, 1.0f, 0.6f) });

        ShowTheme();

        // Tweens: the panel grows out of nothing, its rows slide in one after the other, and the name
        // never stops breathing.
        panel.PopIn(0.5f);
        for (int i = 1; i < panel.Children.Count; i++)
            panel.Children[i].SlideIn(new Vector2(i % 2 == 0 ? 160 : -160, 0), 0.4f, 0.2f + i * 0.06f);
        name.TweenScale(1.08f, 0.9f).SetEasing(Easing.InOutSine).SetLoops(-1, LoopMode.PingPong);
    }

    private void CreateDrifters(SpriteBatch batch)
    {
        Vector2 half = Engine.WindowManager.ViewportSize / 2.0f;

        for (int i = 0; i < DrifterCount; i++)
        {
            string name = Drifters[i % Drifters.Length];
            if (!sprites.TryGetSprite(name, Themes[theme], out var art))
                continue;

            // As big as its art, at the scale the UI draws the pack at.
            var sprite = batch.AddEntity(new Sprite(new Vector2(art.Width, art.Height) * DrifterScale));
            sprite.ConfigureAtlas(spriteAtlas, sprites, name, Themes[theme]);
            sprite.Transform.Position = new Vector2(
                (Random.Shared.NextSingle() * 2 - 1) * half.X,
                (Random.Shared.NextSingle() * 2 - 1) * half.Y);

            // Sure every third one spins, which pixel art only survives with its texels blended at the edges.
            // The ones further back are darker.
            bool spins = i % 3 == 0;
            sprite.Smooth = spins;
            sprite.Tint = new Vector4(new Vector3(0.45f + 0.55f * (i % 4) / 3.0f), 1.0f);

            batch.Add(sprite);
            drifters.Add((
                sprite,
                name,
                new Vector2(Random.Shared.NextSingle() * 2 - 1, Random.Shared.NextSingle() * 2 - 1) * 60.0f,
                spins ? (Random.Shared.NextSingle() * 2 - 1) * 90.0f : 0.0f));
        }
    }

    private void MoveDrifters(float dt)
    {
        // A bit further than the edge of the view, so they are out of sight before they come back in on the other side.
        Vector2 half = Engine.WindowManager.ViewportSize / 2.0f + new Vector2(40);

        foreach (var (sprite, _, velocity, spin) in drifters)
        {
            Vector2 position = sprite.Transform.Position + velocity * dt;

            if (position.X > half.X) position.X -= half.X * 2;
            else if (position.X < -half.X) position.X += half.X * 2;
            if (position.Y > half.Y) position.Y -= half.Y * 2;
            else if (position.Y < -half.Y) position.Y += half.Y * 2;

            sprite.Transform.Position = position;
            if (spin != 0.0f)
                sprite.Transform.Rotation += spin * dt;
        }
    }

    private void ShowTheme()
    {
        themeName.Text = $"{Themes[theme]} theme, press T for the next";

        // The sprites follow: the art of the new theme is added to their atlas as they ask for it.
        foreach (var (sprite, name, _, _) in drifters)
            sprite.ConfigureAtlas(spriteAtlas, sprites, name, Themes[theme]);
    }

    public override void PostInit()
    {
        base.PostInit();
        Engine.GL.ClearColor(0.16f, 0.17f, 0.22f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        MoveDrifters(dt);

        if (Engine.Input.Keyboard.WasPressed(Key.T))
        {
            theme = (theme + 1) % Themes.Length;
            compositor.SetTheme(Themes[theme]);
            ShowTheme();

            // The panel takes the hit, and so does everything that drifts behind it.
            panel.Punch(0.06f, 0.3f);
            foreach (var (sprite, _, _, _) in drifters)
                sprite.Punch(0.5f, 0.4f);
        }

        if (compositor.Skin is { } skin)
            atlasSize.Text = $"the panel is one draw call out of a {skin.Atlas.Size.X} by {skin.Atlas.Size.Y} atlas, the sprites behind it another";
    }
}
