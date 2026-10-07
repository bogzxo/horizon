using Horizon.Engine;

namespace Horizon.Testing;

/// <summary>
/// The bit of the engine an example is about. It's the folder the example lives in under Examples/, and the
/// heading the selector lists it under, in this order.
/// </summary>
internal enum TestArea
{
    /// <summary>Start here: entities, sprites, cameras. The stuff every game needs on day one.</summary>
    Basics,

    /// <summary>Keyboard, mouse and gamepads.</summary>
    Input,

    /// <summary>Everything that ends up as pixels and isn't a UI: tile maps, lights, particles, post processing.</summary>
    Rendering,

    /// <summary>UIX: layouts, skins, controls and scripting.</summary>
    UI,

    /// <summary>Bodies, fixtures and the particle simulators.</summary>
    Physics,

    /// <summary>The guts: threads, frame pacing, tweens and the like.</summary>
    Engine
}

/// <summary>
/// One entry of the test selector.
/// </summary>
/// <param name="Id">What you pass on the command line to start straight in this test, e.g. <c>dotnet run -- tweens</c>.</param>
/// <param name="Area">Which heading the selector lists it under.</param>
/// <param name="Name">What the test's button says.</param>
/// <param name="Description">A line about what the test shows, written next to its button.</param>
/// <param name="Create">
/// Makes a fresh scene every time the test is started. It's a factory and not a scene on purpose: leaving a test
/// throws its scene away, GPU stuff and all, so coming back to it starts from scratch.
/// </param>
internal sealed record TestDefinition(string Id, TestArea Area, string Name, string Description, Func<Scene> Create);
