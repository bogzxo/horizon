/*
 * Good day, and welcome to Horizon!
 *
 * Horizon.Testing is the examples of the engine, one scene per thing it can do, plus a little host that lets you
 * pick one and flick between them. Every example is a plain old Scene, the same kind you'd write for your own game,
 * so read them like a tutorial and steal whatever you like.
 *
 * They come in an order, simplest first, and each one only leans on the ones before it. The selector lists them
 * that way and Host/TestCatalog.cs is the list itself. The levels are the folders under Examples/.
 *   FirstSteps   a window, something drawn, something read off the keyboard. QuickStartExample is forty lines,
 *                start there, then EntitiesExample for which thread runs what, the one thing that will bite you
 *                on the arse if you skip it.
 *   Game         what a game is put together from, tweens, scenes, tile maps, particles, physics, menus.
 *   Lighting     lamps, shadows, the path traced lighting, post processing.
 *   Showcase     the lot in one scene, the way a game has it.
 *   Internals    why things move the way they do.
 * Checks/ is something else, scenes that drive themselves and say whether the engine still does what it did.
 *
 * The art, the maps and the UI layouts are files, in Assets/examples, like the files of a game. Open the maps in
 * Tiled, the layouts in Hex. What is made by a script is made by one in Art/, never as a scene starts.
 *
 * The thirty second version of how Horizon runs, so the comments in the examples make sense.
 *   - Three threads. The one that calls Run() looks after the window and input. The render thread owns the GPU
 *     and draws frames. The simulation thread runs UpdateState and UpdatePhysics, tick after tick, 120 times
 *     a second each unless you say otherwise.
 *   - Frames never read the game while it's running. At the end of every tick everything publishes a snapshot of
 *     itself (Capture), and frames are drawn between the last two ticks, so stuff moves smoothly at any frame rate.
 *   - Constructors never touch the GPU. That happens in Initialize, which runs on the render thread.
 *
 * Running it.
 *   dotnet run                  the selector
 *   dotnet run -- tweens        straight into an example by its id (they're all in Host/TestCatalog.cs)
 *   dotnet run -- --checks      every check one after the other, then out, leaving with how many failed
 *
 * Environment variables worth knowing about.
 *   HORIZON_LOG_LOOPS=5         logs how every loop is doing every 5 seconds (rates, how long turns take)
 *   HORIZON_LOG_ALLOCATIONS=5   logs what every thread allocates, by type, every 5 seconds
 *   HORIZON_SHADER_CACHE=off    compiles every shader anew
 *   HORIZON_INPUT_SCRIPT=file   plays a scripted gamepad from a file, lines like "2.0 tap A" or "8 quit"
 *   HORIZON_SCREENSHOT=f.png@3  saves what was drawn three seconds in
 *
 * Keys that work in every example. Esc for the menu, Page Up and Page Down for the one before and after, F1 folds
 * the list of keys away, F3 goes round the performance overlay (a pill, the whole card, nothing), and in a Debug build
 * F10 brings up the engine's Skyline debugger, the example in a container with the scene tree, an inspector that
 * changes its fields as it runs, what is loaded and what every pass costs around it. F8 pauses, F9 steps a tick.
 */

using Horizon.Core;
using Horizon.Engine;

namespace Horizon.Testing;

internal class Program
{
    private const string CHECKS = "--checks";

    public static void Main(string[] args)
    {
        // A 1600 by 900 window, logic and physics each ticking 120 times a second. Change any of that with a
        // `with` (UpdatesPerSecond, PhysicsUpdatesPerSecond, Presentation, VSync and friends).
        using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "Horizon examples", VSync = false });

        // The host goes on the engine itself rather than in a scene, so it outlives every scene it swaps between
        var host = engine.AddEntity(new TestHost(TestCatalog.Tests));

        // The engine's vital signs over every example, the minimal view to begin with, F3 for the lot and again
        // for none. On the engine like the host, so it is there whatever scene is up. Bottom right, which is the
        // one corner the examples leave alone
        engine.AddEntity(new Horizon.UI.PerformanceOverlay { Corner = Horizon.Rendering.Origin.BottomRight });

        // `dotnet run -- particles` starts straight in an example, by its id. Without one, or with one that doesn't
        // exist, you get the selector.
        if (args.Length > 0 && args[0] == CHECKS)
        {
            host.RunChecks();
        }
        else if (args.Length > 0 && TestCatalog.Find(args[0]) is { } test)
        {
            host.Start(test);
        }
        else
        {
            if (args.Length > 0)
                Console.WriteLine($"There's no example called '{args[0]}'. There are {string.Join(", ", TestCatalog.Tests.Select(t => t.Id))}.");

            host.ShowSelector();
        }

        // Opens the window and doesn't come back until it's closed. From here on this thread only looks after the
        // window and input, the game itself runs on the render and simulation threads.
        engine.Run();
    }
}
