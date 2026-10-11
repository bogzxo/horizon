using System.Numerics;
using System.Text;

using AutoVoxel.Gameplay;
using AutoVoxel.Generation;

using Horizon.Rendering;
using Horizon.Engine;
using Horizon.Logging;
using Horizon.Physics.Colliders;
using Horizon.Rendering.Voxels;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

using Button = Horizon.UI.Components.Button;

namespace AutoVoxel.Scenes;

/// <summary>
/// Playing. The world path traced by a <see cref="VoxelRenderer"/>, the player walking about it through the engine's
/// collision (<see cref="CollisionWorld3D"/> with the tree in it), the day going round, the HUD and the pause menu.
/// <code>
/// Esc     pause, with the options and save in it
/// F1      hide the HUD, for screenshots
/// F3      the engine's performance overlay, F4 the game's debug lines
/// G       bounce light on and off, to see what it does
/// [ ]     an hour back or forward
/// </code>
/// </summary>
public sealed class GameScene(VoxelTree world, WorldInfo info, WorldGenerator? generator, VoxelStreamer? streamer = null) : Scene
{
    private Camera3D camera = null!;
    private VoxelRenderer renderer = null!;
    private Player player = null!;
    private Hud hud = null!;
    private UICompositor ui = null!;
    private UIModule pause = null!, options = null!, loading = null!;
    private Label uploading = null!;
    private bool paused;
    private float debugTimer;
    private readonly StringBuilder debugText = new();

    public VoxelTree World => world;

    /// <summary>Where a tour of the world leaves its screenshots, null for playing (see <see cref="Tour"/>).</summary>
    public static string? TourFolder { get; set; }

    private bool touring;

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        camera = AddEntity(new Camera3D(window.X / window.Y)
        {
            FieldOfView = GameSettings.Current.FieldOfView * MathF.PI / 180.0f,
            NearPlane = 0.05f,
            FarPlane = 30000.0f
        });
        ActiveCamera = camera;

        renderer = AddEntity(new VoxelRenderer((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            World = world,
            Atlas = MainMenuScene.AtlasPath,
            TimeOfDay = info.TimeOfDay,
            SeaLevel = WorldGenerator.SEA_LEVEL
        });
        GameSettings.Current.Apply(renderer);

        // The world is the one solid thing there is
        var collision = new CollisionWorld3D();
        collision.Add(world);

        Vector3 spawn = info.Player;
        if (spawn == Vector3.Zero)
        {
            var (x, z) = generator?.FindSpawn() ?? (world.SizeX / 2, world.SizeZ / 2);
            int y = world.Surface(x, z, countLiquid: false);
            spawn = new Vector3(x + 0.5f, Math.Max(y, WorldGenerator.SEA_LEVEL) + 1.05f, z + 0.5f);
        }

        player = AddComponent(new Player(collision, world, camera, spawn) { Yaw = info.Yaw, Pitch = info.Pitch, IsMade = IsMade });
        AddComponent(player.Body);
        player.Body.Flying = info.Flying;

        ui = AddComponent(UICompositor.ForScreen());
        hud = new Hud(ui, world.Palette, MainMenuScene.AtlasPath);
        hud.ShowHotbar(player.Hotbar, player.Selected, false);
        player.SelectionChanged = () => hud.ShowHotbar(player.Hotbar, player.Selected, true);

        BuildPause();

        // Until the whole world is on the card bits of it are missing, which is said rather than left to look broken
        loading = ui.CreateModule();
        uploading = loading.AddComponent(new Label("") { Anchor = Origin.Top, Position = new Vector2(0, -40), TextScale = 0.3f });

        touring = TourFolder is not null && generator is not null;
        if (touring) AddComponent(new Tour(player, renderer, world, generator!, TourFolder!) { Ready = () => streamer is null || streamer.IsComplete });

        // The rest of the world, made round the player while they play
        if (streamer is not null)
        {
            streamer.Focus = spawn;
            streamer.Start();
        }

        Engine.Input.Mouse.Captured = !touring;
        base.Initialize();
    }

    private void BuildPause()
    {
        pause = ui.CreateModule();
        var panel = pause.AddComponent(new StackPanel { Color = OptionsPanel.PanelColor, Radius = 12, Padding = new UIEdges(28), Spacing = 14, Stretch = true });
        panel.Add(new Label("Paused") { TextScale = 0.5f });
        panel.Add(new Button("Carry on") { OnPressed = () => SetPaused(false) });
        panel.Add(new Button("Options") { OnPressed = () => { pause.Enabled = false; options.Enabled = true; } });
        panel.Add(new Button("Save") { OnPressed = Save });
        panel.Add(new Button("Save and quit to the menu") { OnPressed = () => { Save(); GoTo(new MainMenuScene()); } });
        panel.Add(new Button("Quit without saving") { OnPressed = () => GoTo(new MainMenuScene()) });
        pause.Enabled = false;

        options = ui.CreateModule();
        OptionsPanel.Build(options, () => renderer, () => { options.Enabled = false; pause.Enabled = true; });
        options.Enabled = false;
    }

    private void SetPaused(bool value)
    {
        paused = value;
        pause.Enabled = value;
        options.Enabled = false;
        player.Controlling = !value;
        Engine.Input.Mouse.Captured = !value;
        camera.FieldOfView = GameSettings.Current.FieldOfView * MathF.PI / 180.0f;
        player.Body.AutoJump = GameSettings.Current.AutoJump;
    }

    private void Save()
    {
        info.PlayerX = player.Body.Position.X;
        info.PlayerY = player.Body.Position.Y;
        info.PlayerZ = player.Body.Position.Z;
        info.Yaw = player.Yaw;
        info.Pitch = player.Pitch;
        info.TimeOfDay = renderer.TimeOfDay;
        info.Flying = player.Body.Flying;

        try
        {
            WorldFiles.Save(world, info, streamer is { IsComplete: false } ? streamer.Done : null);
            Log.Info($"[AutoVoxel] Saved '{info.Name}' to {WorldFiles.Directory}.");
        }
        catch (Exception e)
        {
            Log.Error($"[AutoVoxel] '{info.Name}' couldn't be saved: {e.Message}");
        }
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        if (IsLeaving) return;

        var keys = Engine.Input.Keyboard;
        if (keys.WasPressed(Key.Escape))
        {
            if (options.Enabled)
            {
                options.Enabled = false;
                pause.Enabled = true;
            }
            else SetPaused(!paused);
        }

        if (keys.WasPressed(Key.F1)) hud.Visible = !(hudShown = !hudShown) ? false : true;
        if (keys.WasPressed(Key.F4)) hud.DebugVisible = !hud.DebugVisible;
        if (keys.WasPressed(Key.G)) renderer.BounceLight = !renderer.BounceLight;
        if (keys.WasPressed(Key.LeftBracket)) renderer.TimeOfDay = (renderer.TimeOfDay + 23.0f) % 24.0f;
        if (keys.WasPressed(Key.RightBracket)) renderer.TimeOfDay = (renderer.TimeOfDay + 1.0f) % 24.0f;

        // The day goes round
        if (!paused && !touring) renderer.TimeOfDay = (renderer.TimeOfDay + dt * 24.0f / (60.0f * MathF.Max(GameSettings.Current.DayMinutes, 0.5f))) % 24.0f;

        renderer.Selected = player.Target is { } target ? (target.X, target.Y, target.Z) : null;

        // The world is made round the player, wherever they've got to, and nobody falls into a bit that isn't made yet
        if (streamer is not null)
        {
            Vector3 at = player.Body.Position;
            streamer.Focus = at;
            if (!streamer.IsReady((int)at.X, (int)at.Z) && player.Body.Velocity.Y < 0.0f)
                player.Body.Velocity = player.Body.Velocity with { Y = 0.0f };
        }
        renderer.Underwater = player.Body.IsUnderwater;

        hud.Update(dt);
        uploading.Text = renderer.UploadProgress < 1.0f ? $"sending the world to the GPU, {renderer.UploadProgress * 100:0}%"
            : streamer is { IsComplete: false } ? $"making the world, {streamer.Progress * 100:0}%" : string.Empty;

        if (hud.DebugVisible && (debugTimer -= dt) <= 0.0f)
        {
            debugTimer = 0.25f;
            hud.SetDebug(Debug());
        }
    }

    private bool hudShown = true;

    private string Debug()
    {
        Vector3 p = player.Body.Position;
        debugText.Clear();
        debugText.AppendLine($"AutoVoxel  {info.Name}  seed {info.Seed}");
        debugText.AppendLine($"at {p.X:0.0} {p.Y:0.0} {p.Z:0.0}  facing {player.Yaw * 180 / MathF.PI % 360:0}  {(player.Body.Flying ? "flying" : player.Body.IsSwimming ? "swimming" : player.Body.IsGrounded ? "on the ground" : "in the air")}");
        if (generator is not null)
        {
            var column = generator.ColumnAt((int)p.X, (int)p.Z);
            debugText.AppendLine($"biome {column.Biome}  ground {column.Height}");
        }

        debugText.AppendLine($"time {(int)renderer.TimeOfDay:00}:{(int)(renderer.TimeOfDay % 1 * 60):00}");
        if (player.Target is { } t) debugText.AppendLine($"looking at {world.Palette[t.Material].Name} {t.X} {t.Y} {t.Z}");
        debugText.AppendLine($"world {world.SizeX} x {world.SizeY} x {world.SizeZ}, {world.NodeCount:N0} nodes, {world.MemoryBytes / 1048576.0:0} MB, {world.Garbage:N0} left over");
        debugText.AppendLine($"traced at {renderer.TraceSize.X} x {renderer.TraceSize.Y}, bounce light {(renderer.BounceLight ? "on" : "off")}, {renderer.GpuBytes / 1048576.0:0} MB on the card");
        return debugText.ToString();
    }

    /// <summary>Whether the column a block is in has been made yet, nothing can be done to one that hasn't.</summary>
    private bool IsMade(int x, int z) => streamer is null || streamer.IsReady(x, z);

    protected override void DisposeOther()
    {
        // The threads making the world stop before the world goes
        streamer?.Dispose();
        Engine.Input.Mouse.Captured = false;
        base.DisposeOther();
    }
}
