namespace Horizon.Engine.Components;

/// <summary>
/// What becomes of a scene while another one is on top of it, see <see cref="SceneManager.PushScene"/>. The two
/// can be had together, a scene that is drawn and updated underneath carries on as if nothing were on top of it
/// but for not being the one that is listened to.
/// </summary>
[Flags]
public enum SceneBelow
{
    /// <summary>
    /// It stands still and isn't seen, kept exactly as it was left. For a screen that takes the whole window
    /// over a game that has nothing to do in the meantime.
    /// </summary>
    Suspended = 0,

    /// <summary>
    /// It is still drawn, behind the scene on top, which makes that one an overlay. For a menu with the game
    /// showing through behind it.
    /// </summary>
    Drawn = 1,

    /// <summary>
    /// It still gets its updates and its physics. For a game that can't stop because somebody opened a menu,
    /// a fight over a network that has to keep talking to the other machine. The scene tells that it is
    /// underneath by <see cref="Scene.IsSuspended"/> and keeps its hands off the buttons.
    /// </summary>
    Updated = 2,

    /// <summary>Drawn and updated, an overlay over a game that carries on.</summary>
    Running = Drawn | Updated
}
