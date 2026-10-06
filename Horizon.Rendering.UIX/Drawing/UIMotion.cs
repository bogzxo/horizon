using System.Numerics;

namespace Horizon.Rendering.UIX.Drawing;

/// <summary>
/// Works out how fast a component is going across the screen from where it is drawn every update, for the effects
/// that blur what moves (see <see cref="UICompositor.PostProcessing"/>).
/// It follows two opposite corners of the component rather than its middle, so growing and shrinking are motion
/// too: something that pops in is going outwards at its edges and nowhere at its middle.
/// Not everything that ends up somewhere else has moved there though. A list that jumps a row when the wheel is
/// turned, a label that gets wider with what it says, a panel that is shown again somewhere new: none of that is
/// to be smeared. So a single step counts for nothing, it takes a second one right after it to call it moving, and
/// so does anything the layout did by giving the component another size.
/// </summary>
internal sealed class UIMotion
{
    // How much of a new measurement is taken over, the rest stays what it was
    private const float SMOOTHING = 0.5f;

    // Further than this in one update (in units of the camera) is being put somewhere else, however often it happens
    private const float JUMP = 192.0f;

    private Vector2 lastMin, lastMax, lastSize;
    private int lastFrame = -2;
    private bool moving;

    /// <summary>How fast the corner the component starts at is going, in units of the camera a second.</summary>
    public Vector2 Min { get; private set; }

    /// <summary>How fast the opposite corner is going.</summary>
    public Vector2 Max { get; private set; }

    /// <param name="min">Where the corner the component starts at is drawn right now, as the camera sees it.</param>
    /// <param name="max">Where the opposite corner is drawn.</param>
    /// <param name="layoutSize">How big the layout made the component, whatever size it is drawn at.</param>
    /// <param name="frame">Which update this is, counting up by one for as long as the UI is updated without a break.</param>
    /// <param name="dt">How long the update is, in seconds.</param>
    public void Track(Vector2 min, Vector2 max, Vector2 layoutSize, int frame, float dt)
    {
        Vector2 minStep = min - lastMin, maxStep = max - lastMax;

        // Drawn the update before this one, and the size it was then
        bool followed = frame == lastFrame + 1 && layoutSize == lastSize && dt > 0.0f;

        bool moved = followed
            && (minStep != Vector2.Zero || maxStep != Vector2.Zero)
            && minStep.LengthSquared() < JUMP * JUMP
            && maxStep.LengthSquared() < JUMP * JUMP;

        if (moved && moving)
        {
            Min = Vector2.Lerp(Min, minStep / dt, SMOOTHING);
            Max = Vector2.Lerp(Max, maxStep / dt, SMOOTHING);
        }
        else
        {
            // Standing still, or one step into what may yet turn out to be moving
            Min = Max = Vector2.Zero;
        }

        moving = moved;
        (lastMin, lastMax, lastSize, lastFrame) = (min, max, layoutSize, frame);
    }
}
