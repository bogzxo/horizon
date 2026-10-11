using AutoVoxel.Gameplay;
using AutoVoxel.Scenes;

using Horizon.Core;
using Horizon.Engine;
using Horizon.UI;

namespace AutoVoxel;

/// <summary>
/// AutoVoxel, the block game. Back from the dead (it was a Minecraft clone on the OpenGL engine in 2024, meshed
/// chunks and all) as a path traced one, the whole world in the card as a sparse 64-tree and drawn at once.
/// <code>
/// AutoVoxel                      the menu
/// AutoVoxel --play 2048 [seed]   straight into a new world of that size, for testing
/// AutoVoxel --continue           straight into the last saved world
/// AutoVoxel --play 2048 --tour shots   a tour of a new world, a screenshot of every stop, then out
/// AutoVoxel --size 1920x1080     a window of that size
/// </code>
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        GameSettings.Load();

        // --size 1920x1080 for a window of another size than the usual
        var window = new System.Numerics.Vector2(1600, 900);
        int sized = Array.IndexOf(args, "--size");
        if (sized >= 0 && sized + 1 < args.Length && args[sized + 1].Split('x') is [var w, var h] && int.TryParse(w, out int width) && int.TryParse(h, out int height))
            window = new System.Numerics.Vector2(width, height);

        var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with
        {
            WindowTitle = "AutoVoxel",
            WindowSize = window,
            VSync = GameSettings.Current.VSync
        });

        // F3 for the frame rate and what every pass costs
        engine.AddEntity(new PerformanceOverlay());

        int tour = Array.IndexOf(args, "--tour");
        if (tour >= 0) GameScene.TourFolder = tour + 1 < args.Length ? args[tour + 1] : "tour";

        Scene first = new MainMenuScene();
        int play = Array.IndexOf(args, "--play");
        if (play >= 0)
        {
            int size = play + 1 < args.Length && int.TryParse(args[play + 1], out int s) ? s : GameSettings.Current.WorldSize;
            int seed = play + 2 < args.Length && int.TryParse(args[play + 2], out int n) ? n : 1337;
            first = new LoadingScene(new WorldInfo { Name = $"test {size}", Seed = seed, Size = size }, generate: true);
        }
        else if (args.Contains("--continue") && WorldFiles.Latest() is { } latest)
        {
            first = new LoadingScene(latest, generate: false);
        }

        engine.Run(first);
    }
}
