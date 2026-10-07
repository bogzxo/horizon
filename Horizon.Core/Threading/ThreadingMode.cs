namespace Horizon.Core.Threading;

/// <summary>
/// How drawing and simulating share the game.
/// </summary>
public enum ThreadingMode : byte
{
    /// <summary>
    /// They take turns: the simulation stands still while a frame is drawn, and a frame is drawn from the game as it is.
    /// Everything that draws can read whatever it likes, and a frame never shows half of a tick. What every engine
    /// renderer and every game did before the engine was decoupled, and what is left to fall back on while not
    /// everything that draws has been made to draw from snapshots.
    /// </summary>
    Lockstep,

    /// <summary>
    /// Neither ever waits for the other. The simulation publishes a snapshot of what is drawn at the end of every tick
    /// and the frames are drawn from those (see <see cref="SnapshotClock"/>), shown between the last two however the
    /// ticks and the frames line up (see <see cref="PresentationMode"/>). Only the setting up of what was added to the
    /// game and the swapping of scenes have the simulation stand still for a moment, at the end of a tick.
    /// </summary>
    Decoupled
}
