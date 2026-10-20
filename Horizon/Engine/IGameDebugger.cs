#if DEBUG
using Silk.NET.Input;

namespace Horizon.Engine;

/// <summary>
/// What the engine knows of the debugger a Debug build has over the game, the Skyline debugger. It's drawn with the
/// UI, so it lives in Horizon.UI, which says how one is made as it loads (<see cref="GameEngine.DebuggerMaker"/>). The
/// engine adds it to itself and draws it last, and this is what a game can say to it.
/// </summary>
public interface IGameDebugger
{
    /// <summary>Whether it is on screen. From any thread.</summary>
    bool Shown { get; set; }

    /// <summary>The key that shows and hides it. Null for none.</summary>
    Key? ToggleKey { get; set; }

    /// <summary>The key that pauses the scene. Null for none.</summary>
    Key? PauseKey { get; set; }

    /// <summary>The key that steps a paused scene a tick. Null for none.</summary>
    Key? StepKey { get; set; }

    /// <summary>Drawn over everything, after the rest of the frame.</summary>
    void Draw(float dt);
}
#endif
