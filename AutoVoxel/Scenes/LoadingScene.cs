using System.Diagnostics;
using System.Numerics;

using AutoVoxel.Gameplay;
using AutoVoxel.Generation;

using Horizon.Engine;
using Horizon.Logging;
using Horizon.Rendering.Voxels;
using Horizon.UI;
using Horizon.UI.Components;

namespace AutoVoxel.Scenes;

/// <summary>
/// Getting ready to play. A new world is only the ground round where the player starts, a few chunks each way, made on
/// every core in a moment, the rest of it is made while they play (<see cref="VoxelStreamer"/>, in rings round them). A
/// saved one is read off the disk, and if it was saved before it was all made the making carries on in the game.
/// Hands over to the <see cref="GameScene"/> once it's there.
/// </summary>
public sealed class LoadingScene(WorldInfo info, bool generate) : Scene
{

    // How many chunks round the spawn are made before the player is put there
    private const int SPAWN_RADIUS = 3;

    private Task<(VoxelTree Tree, VoxelStreamer? Streamer)>? work;
    private ProgressBar bar = null!;
    private Label status = null!;
    private readonly Stopwatch clock = new();
    private WorldGenerator? generator;

    public override void Initialize()
    {
        ActiveCamera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        Engine.Input.Mouse.Captured = false;

        var ui = AddComponent(UICompositor.ForScreen());
        var module = ui.CreateModule();
        var panel = module.AddComponent(new StackPanel { Color = OptionsPanel.PanelColor, Radius = 12, Padding = new UIEdges(32), Spacing = 18, Stretch = true });
        panel.Add(new Label(generate ? $"Making '{info.Name}'" : $"Reading '{info.Name}'") { TextScale = 0.5f });
        panel.Add(new Label(generate ? $"{info.Size} x 256 x {info.Size}, seed {info.Seed}" : "off the disk") { TextScale = 0.22f, Color = new Vector4(1, 1, 1, 0.7f) });
        bar = panel.Add(new ProgressBar { Size = new Vector2(520, 28), ShowText = false, Progress = 0.5f });
        status = panel.Add(new Label("") { TextScale = 0.22f });

        var palette = Blocks.Blocks.CreatePalette();
        clock.Start();

        generator = new WorldGenerator(info.Seed, info.Size, 256, info.Size);
        WorldGenerator g = generator;
        WorldInfo i = info;
        work = generate ? Task.Run(() => Begin(palette, g, i)) : Task.Run(() => Resume(palette, g, i));

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        if (work is null || IsLeaving) return;

        status.Text = $"{clock.Elapsed.TotalSeconds:0.0} s";

        if (work.IsFaulted)
        {
            Log.Error($"[AutoVoxel] The world couldn't be made: {work.Exception}");
            status.Text = $"That went wrong, {work.Exception?.InnerException?.Message}";
            work = null;
            return;
        }

        if (!work.IsCompletedSuccessfully) return;

        var (tree, streamer) = work.Result;
        Log.Info($"[AutoVoxel] '{info.Name}' is {tree.SizeX} x {tree.SizeY} x {tree.SizeZ}, ready in {clock.Elapsed.TotalSeconds:0.00} s, " +
                 $"{(streamer is null ? "all of it" : $"{streamer.ColumnsDone:N0} of {streamer.ColumnsTotal:N0} columns")} made, {tree.NodeCount:N0} nodes so far.");
        GoTo(new GameScene(tree, info, generator, streamer));
        work = null;
    }

    /// <summary>A new world, empty but for the ground round the spawn.</summary>
    private static (VoxelTree, VoxelStreamer?) Begin(Horizon.Rendering.Voxels.VoxelPalette palette, WorldGenerator generator, WorldInfo info)
    {
        int size = info.Size;
        var tree = VoxelTree.Empty(size, 256, size, palette, VoxelStreamer.EstimateNodes(size, size), VoxelStreamer.EstimateVoxels(size, size));
        var streamer = new VoxelStreamer(tree, generator);

        var (x, z) = generator.FindSpawn();
        var spawn = new Vector3(x, 0, z);
        streamer.Focus = spawn;
        streamer.MakeAround(spawn, SPAWN_RADIUS);
        return (tree, streamer);
    }

    /// <summary>A saved world, compacted (the game saves without, see WorldFiles), and its making carried on if it wasn't finished.</summary>
    private static (VoxelTree, VoxelStreamer?) Resume(Horizon.Rendering.Voxels.VoxelPalette palette, WorldGenerator generator, WorldInfo info)
    {
        VoxelTree tree = WorldFiles.Load(info, palette);
        tree.Compact();

        if (WorldFiles.LoadColumns(info) is not { } done) return (tree, null);

        var streamer = new VoxelStreamer(tree, generator, alreadyDone: done);
        streamer.Focus = info.Player;
        streamer.MakeAround(info.Player, SPAWN_RADIUS);
        return (tree, streamer);
    }
}
