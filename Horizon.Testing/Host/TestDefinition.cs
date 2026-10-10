using Horizon.Engine;

namespace Horizon.Testing;

/// <summary>
/// How far along an example is, which is the order the selector lists them in and the folder they live in under
/// Examples/. Each level only leans on the ones before it, so reading them top to bottom never has you looking at
/// something that hasn't been explained yet.
/// </summary>
internal enum TestLevel
{
    /// <summary>A window, something drawn in it, something read off the keyboard. Day one.</summary>
    FirstSteps,

    /// <summary>What a game is put together from, maps, scenes, particles, physics, menus.</summary>
    MakingAGame,

    /// <summary>Lights, shadows, the path traced lighting and what goes over the finished picture.</summary>
    LightAndEffects,

    /// <summary>The lot in one scene, the way a game would have it.</summary>
    AllTogether,

    /// <summary>The guts, for when you want to know why something moves the way it does.</summary>
    UnderTheHood,

    /// <summary>Not examples. Scenes that drive themselves and say whether the engine still does what it did.</summary>
    Checks
}

/// <summary>
/// One entry of the selector.
/// </summary>
/// <param name="Id">What you pass on the command line to start straight in it, <c>dotnet run -- tweens</c>.</param>
/// <param name="Level">Which heading the selector lists it under.</param>
/// <param name="Name">What its row says.</param>
/// <param name="Summary">A sentence or two about what there is to see, shown next to the list when it is picked.</param>
/// <param name="Shows">The parts of the engine it is about, by the names they go by in code, so they can be searched for.</param>
/// <param name="Source">Where its scene is, from the Horizon.Testing folder.</param>
/// <param name="Create">
/// Makes a fresh scene every time it is started. A factory and not a scene on purpose, leaving an example throws
/// its scene away, GPU stuff and all, so coming back to it starts from scratch.
/// </param>
internal sealed record TestDefinition(string Id, TestLevel Level, string Name, string Summary, string[] Shows, string Source, Func<Scene> Create)
{
    /// <summary>Where it comes in the list, counted from 1. The checks have no number, they aren't a lesson.</summary>
    public int Number { get; init; }

    /// <summary>The other names it answers to on the command line, the ids the examples it replaced went by.</summary>
    public string[] Aliases { get; init; } = [];
}

/// <summary>
/// For a scene that checks something by itself and can say how it went, which is what lets the host run every
/// check one after the other and leave (<c>Horizon.Testing --checks</c>).
/// </summary>
internal interface ISelfCheck
{
    /// <summary>Whether it has checked everything it was going to.</summary>
    bool Finished { get; }

    /// <summary>How many of its checks didn't hold.</summary>
    int Failed { get; }

    /// <summary>How many it made.</summary>
    int Count { get; }
}
