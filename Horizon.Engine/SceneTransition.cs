using Horizon.Core.Tweening;

namespace Horizon.Engine;

/// <summary>
/// How one scene hands over to the next. Without one a scene change is a hard cut.
/// With one the old scene gets covered up first, the scenes are swapped while nobody can see, and then the new one is uncovered.
/// Set <see cref="Components.SceneManager.Transition"/> once and every scene change goes through it, or hand one to SetScene for just that change.
/// <para>
/// This only says how long covering and uncovering take, the scene manager moves the cover along with a tween.
/// What covering a scene up looks like is up to whoever derives from it. Horizon.Rendering comes with a fade, a blur and a rot,
/// and its ScreenTransition is the place to start for anything else that works on the finished frame.
/// </para>
/// </summary>
public abstract class SceneTransition
{
    /// <summary>How long (in seconds) the old scene takes to get covered up. Zero swaps straight away, the old scene still gets one last frame.</summary>
    public float OutTime { get; set; } = 0.2f;

    /// <summary>How long (in seconds) the new scene takes to get uncovered again.</summary>
    public float InTime { get; set; } = 0.3f;

    /// <summary>The curves the covering and the uncovering follow.</summary>
    public Easing OutEasing { get; set; } = Easing.InQuad;
    public Easing InEasing { get; set; } = Easing.OutQuad;

    /// <summary>
    /// Draws the transition over the finished frame. Render thread, once a frame for as long as it runs.
    /// The last frame of the old scene always comes through here with a cover of exactly 1, and so does the first frame of the new one.
    /// A transition that wants the two to match up (a crossfade, a dissolve) keeps what it needs of the old scene from the one for the other.
    /// </summary>
    /// <param name="cover">How much of the scene is hidden, from 0 for none of it to 1 for all of it.</param>
    /// <param name="arriving">False while the old scene is being covered up, true once the new scene is under there and is being uncovered.</param>
    /// <param name="dt">How long the frame is, in seconds.</param>
    public abstract void Render(float cover, bool arriving, float dt);

    /// <summary>
    /// Told once the transition has nothing left to draw. Either the new scene is all the way uncovered, or another transition took over half way.
    /// Render thread. Whatever was only kept for the occasion (a picture of the old scene) is let go of here, the transition may well be used again later.
    /// </summary>
    public virtual void Finish()
    { }
}
