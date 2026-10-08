/*
 * Good day, and welcome to Horizon!
 *
 * Horizon.Testing is a pile of example scenes, one per feature of the engine, plus a little host that lets you pick
 * one and flick between them. Every example is a plain old Scene, the same kind you'd write for your own game, so
 * read them like a tutorial and steal whatever you like.
 *
 * Where to start reading (Examples/Basics, in this order):
 *   1. EntitiesExample - entities, components and which thread runs what. Read this one first, honestly. The
 *      threading is the one thing that will bite you on the arse if you skip it.
 *   2. SpritesExample  - getting things on screen.
 *   3. CameraExample   - Camera2D, and moving around a world bigger than the window.
 * After that go wherever you like. The selector groups the rest by area: Input, Rendering, UI, Physics, Engine,
 * which are also the folders under Examples/.
 *
 * The thirty second version of how Horizon runs, so the comments in the examples make sense:
 *   - Three threads. The one that calls Run() looks after the window and input. The render thread owns the GL
 *     context and draws frames. The simulation thread runs UpdateState and UpdatePhysics, tick after tick, 120 times
 *     a second each unless you say otherwise.
 *   - Frames never read the game while it's running. At the end of every tick everything publishes a snapshot of
 *     itself (Capture), and frames are drawn between the last two ticks, so stuff moves smoothly at any frame rate.
 *   - Constructors never touch the GPU. That happens in Initialize, which runs on the render thread.
 *
 * Running it:
 *   dotnet run                  the selector
 *   dotnet run -- tweens        straight into a test by its id (they're all in Host/TestCatalog.cs)
 *
 * Environment variables worth knowing about:
 *   HORIZON_LOG_LOOPS=5         logs how every loop is doing every 5 seconds (rates, how long turns take)
 *   HORIZON_LOG_ALLOCATIONS=5   logs what every thread allocates, by type, every 5 seconds
 *   HORIZON_SHADER_CACHE=off    stops linked shader programs being cached on disk (or set it to a folder to keep them there)
 *   HORIZON_INPUT_SCRIPT=file   plays a scripted gamepad from a file, lines like "2.0 tap A" or "8 quit"
 *
 * Keys that work everywhere: F3 cycles the performance overlay (where a test adds one), F4 opens the engine's console.
 */

using Horizon.Core;
using Horizon.Engine;

namespace Horizon.Testing;

internal class Program
{
    public static void Main(string[] args)
    {
        // A 1600 by 900 window with vsync, logic and physics each ticking 120 times a second. Change any of that
        // with a `with` (UpdatesPerSecond, PhysicsUpdatesPerSecond, Presentation, VSync and friends).
        using var engine = new GameEngine(WindowManagerConfiguration.Default1600x900 with { WindowTitle = "Horizon examples", VSync = false });

        // The host goes on the engine itself rather than in a scene, so it outlives every scene it swaps between
        var host = engine.AddEntity(new TestHost(TestCatalog.Tests));

        // `dotnet run -- particles` starts straight in a test, by its id. Without one, or with one that doesn't
        // exist, you get the selector.
        if (args.Length > 0 && TestCatalog.Find(args[0]) is { } test)
        {
            host.Start(test);
        }
        else
        {
            if (args.Length > 0)
                Console.WriteLine($"There's no test called '{args[0]}'. The tests are: {string.Join(", ", TestCatalog.Tests.Select(t => t.Id))}.");

            host.ShowSelector();
        }

        // Opens the window and doesn't come back until it's closed. From here on this thread only looks after the
        // window and input, the game itself runs on the render and simulation threads.
        engine.Run();
    }
}
