using System.Numerics;

using AutoVoxel.Gameplay;
using AutoVoxel.Generation;

using Horizon.Rendering;
using Horizon.Engine;
using Horizon.Rendering.Voxels;
using Horizon.UI;
using Horizon.UI.Components;

namespace AutoVoxel.Scenes;

/// <summary>
/// The title. A small world is made as the menu comes up (half a second or so) and the camera drifts over it at
/// golden hour, path traced like the real thing, so the first thing anybody sees is what the game looks like. On top
/// of it, new world, carry on with the last one, the options and the way out.
/// </summary>
public sealed class MainMenuScene : Scene
{
    // How big the world behind the menu is
    private const int BACKDROP = 512;

    private Camera3D camera = null!;
    private VoxelRenderer renderer = null!;
    private UICompositor ui = null!;
    private UIModule title = null!, main = null!, create = null!, options = null!;
    private Task<VoxelTree>? backdrop;
    private WorldGenerator? generator;
    private float time, height = 120.0f;

    public static string AtlasPath => Path.Combine(AppContext.BaseDirectory, "content", "atlas.png");

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;
        Engine.Input.Mouse.Captured = false;

        camera = AddEntity(new Camera3D(window.X / window.Y) { FieldOfView = 70.0f * MathF.PI / 180.0f, FarPlane = 20000.0f, Position = new Vector3(256, 140, 256) });
        ActiveCamera = camera;

        renderer = AddEntity(new VoxelRenderer((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            Atlas = AtlasPath,
            TimeOfDay = 16.6f,
            Cloudiness = 0.35f,
            SeaLevel = WorldGenerator.SEA_LEVEL
        });
        GameSettings.Current.Apply(renderer);

        ui = AddComponent(UICompositor.ForScreen());
        BuildMenus();

        int seed = Random.Shared.Next();
        generator = new WorldGenerator(seed, BACKDROP, 256, BACKDROP);
        WorldGenerator g = generator;
        backdrop = Task.Run(() => VoxelTree.Generate(BACKDROP, 256, BACKDROP, Blocks.Blocks.CreatePalette(), g));

        base.Initialize();
    }

    private void BuildMenus()
    {
        title = ui.CreateModule();
        title.AddComponent(new Label("AutoVoxel") { Anchor = Origin.Top, Position = new Vector2(0, -120), TextScale = 1.2f });
        title.AddComponent(new Label("a whole world, path traced, all at once") { Anchor = Origin.Top, Position = new Vector2(0, -210), TextScale = 0.3f, Color = new Vector4(1, 1, 1, 0.8f) });

        main = ui.CreateModule();
        main.Position = new Vector2(0, -60);
        var panel = main.AddComponent(new StackPanel { Color = OptionsPanel.PanelColor, Radius = 12, Padding = new UIEdges(28), Spacing = 14, Stretch = true });
        panel.Add(new Button("New world") { OnPressed = () => Show(create) });

        WorldInfo? latest = WorldFiles.Latest();
        panel.Add(new Button(latest is null ? "Continue" : $"Continue '{latest.Name}'")
        {
            Enabled = latest is not null,
            OnPressed = () => { if (latest is not null) GoTo(new LoadingScene(latest, generate: false)); }
        });

        panel.Add(new Button("Options") { OnPressed = () => Show(options) });
        panel.Add(new Button("Quit") { OnPressed = Engine.Exit });

        // A new world, how big and from what seed
        create = ui.CreateModule();
        create.Position = new Vector2(0, -60);
        var form = create.AddComponent(new StackPanel { Color = OptionsPanel.PanelColor, Radius = 12, Padding = new UIEdges(28), Spacing = 14, Stretch = true });
        form.Add(new Label("New world") { TextScale = 0.5f });

        var size = form.Add(new Selector([.. GameSettings.WorldSizes.Select(s => $"{s} x {s}")])
        {
            Index = Math.Max(0, Array.IndexOf(GameSettings.WorldSizes, GameSettings.Current.WorldSize)),
            Size = new Vector2(360, 48),
            TextScale = 0.26f
        });
        var hint = form.Add(new Label(SizeHint(GameSettings.Current.WorldSize)) { TextScale = 0.2f, Color = new Vector4(1, 1, 1, 0.7f) });
        size.OnChanged = _ =>
        {
            GameSettings.Current.WorldSize = GameSettings.WorldSizes[size.Index];
            hint.Text = SizeHint(GameSettings.Current.WorldSize);
            GameSettings.Save();
        };

        var name = form.Add(new TextBox("world") { Placeholder = "name", Size = new Vector2(360, 48), TextScale = 0.26f, MaxLength = 32 });
        var seed = form.Add(new TextBox { Placeholder = "seed (random if empty)", Size = new Vector2(360, 48), TextScale = 0.26f, MaxLength = 18 });

        form.Add(new Button("Make it") { OnPressed = () => Create(name.Text, seed.Text) });
        form.Add(new Button("Back") { OnPressed = () => Show(main) });

        options = ui.CreateModule();
        OptionsPanel.Build(options, () => renderer, () => Show(main));

        Show(main);
    }

    private static string SizeHint(int size) => size switch
    {
        1024 => "a big island, all made a second after you're in",
        2048 => "a continent, all made a few seconds after you're in",
        4096 => "sixteen square kilometres, made round you as you play, about a GB",
        _ => "sixty four square kilometres, made round you for half a minute, a few GB",
    };

    private void Create(string name, string seedText)
    {
        name = string.Concat((string.IsNullOrWhiteSpace(name) ? "world" : name.Trim()).Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'));
        int seed = string.IsNullOrWhiteSpace(seedText) ? Random.Shared.Next()
            : int.TryParse(seedText, out int number) ? number : seedText.GetHashCode();

        var info = new WorldInfo { Name = name, Seed = seed, Size = GameSettings.Current.WorldSize };
        GoTo(new LoadingScene(info, generate: true));
    }

    private void Show(UIModule which)
    {
        main.Enabled = which == main;
        create.Enabled = which == create;
        options.Enabled = which == options;
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        time += dt;

        if (backdrop is { IsCompletedSuccessfully: true } && renderer.World is null)
        {
            VoxelTree tree = backdrop.Result;
            renderer.World = tree;
            height = Math.Max(tree.Surface(BACKDROP / 2, BACKDROP / 2), WorldGenerator.SEA_LEVEL) + 55.0f;
        }

        // Round the middle of the little world, slowly, looking down at it
        float angle = time * 0.035f;
        var center = new Vector3(BACKDROP / 2.0f, height - 45.0f, BACKDROP / 2.0f);
        camera.Position = center + new Vector3(MathF.Cos(angle) * 170.0f, 45.0f + MathF.Sin(time * 0.1f) * 6.0f, MathF.Sin(angle) * 170.0f);
        camera.LookAt(center + new Vector3(0.0f, -10.0f, 0.0f));
    }
}
