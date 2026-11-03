using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Rendering.Spriting;

namespace Horizon.UI.Drawing;

/// <summary>
/// What the UI looks like at a moment between two ticks, from the frames the two ticks painted. The compositor shows a
/// frame through this, and the tests ask it where things are drawn halfway between two ticks without a GPU. One of
/// these keeps its buffers from one frame to the next, so putting a frame together makes no garbage.
/// </summary>
internal sealed class UIInBetween
{
    // The frames between two ticks and what they resolve to, and where every frame of the older tick is by its key
    private readonly List<UIFrame> between = [];
    private readonly Dictionary<Vector2, int> framesBefore = [];
    private SpriteItem[] resolved = [];
    private readonly List<UIDrawList.Run> resolvedRuns = [];
    private readonly List<UIGlassPane> resolvedGlass = [];
    private int resolvedCount;

    // How far every frame is moved to go where the pointer is, not a number for the ones that don't follow it
    private Vector2[] followed = [];

    /// <summary>
    /// How far a frame may move on screen from one tick to the next and still be moved there smoothly, in pixels. Further
    /// is a jump (the code gone to its end), and moved halfway there in a frame between it showed the empty space past
    /// what the newer tick painted.
    /// </summary>
    public const float JUMP = 256.0f;

    /// <summary>
    /// A frame between two ticks put together from their frames. Every frame of the newer tick is moved to where it
    /// was between the two, by its key, and the newer tick's quads are resolved in them, clipped where their clips are
    /// then. So what's in a frame is always the newer tick's, quads that came and went with a hover don't move anything
    /// else, a row coming into sight under a clip comes in with the rest, and a picture cut by a clip's edge moves with
    /// its neighbours. A frame the older tick hadn't is where the newer one has it. What it comes to is in
    /// <see cref="FramedQuads"/>, <see cref="FramedRuns"/> and <see cref="FramedGlass"/> until the next call.
    /// </summary>
    /// <param name="pointer">Where the pointer is now, for the frames that go where it goes (see <see cref="UIFrame.Follows"/>), null for them to stay where they were painted.</param>
    public void Frames(UIFrameList a, UIFrameList b, float alpha, Vector2? pointer = null)
    {
        framesBefore.Clear();
        for (int i = 0; i < a.Frames.Count; i++)
            framesBefore.TryAdd(a.Frames[i].Key, i);

        between.Clear();
        foreach (UIFrame frame in b.Frames)
            between.Add(framesBefore.TryGetValue(frame.Key, out int was) ? Between(a.Frames[was], frame, alpha) : frame);

        if (pointer is { } now && b.Follows)
            Follow(b, now);

        resolvedCount = b.Resolve(CollectionsMarshal.AsSpan(between), ref resolved, resolvedRuns, resolvedGlass);
    }

    public ReadOnlySpan<SpriteItem> FramedQuads => resolved.AsSpan(0, resolvedCount);
    public ReadOnlySpan<UIDrawList.Run> FramedRuns => CollectionsMarshal.AsSpan(resolvedRuns);
    public ReadOnlySpan<UIGlassPane> FramedGlass => CollectionsMarshal.AsSpan(resolvedGlass);

    /// <summary>
    /// Helper method for a frame between where two ticks had it. One that only moved (nothing in it grew) moves by whole
    /// pixels from where the newer tick has it, so it keeps its place between the pixels and everything that moved with
    /// it stays where it was against it. Moved by the fraction of the way between two ticks it came out somewhere else
    /// between the pixels every frame, and a scroll shimmered. One that jumped further than <see cref="JUMP"/> is where
    /// the newer tick put it.
    /// </summary>
    internal static UIFrame Between(in UIFrame a, in UIFrame b, float t)
    {
        Vector2 across = b.VisualScale * b.Scale;
        float opacity = a.Opacity + (b.Opacity - a.Opacity) * t;
        if (a.VisualScale == b.VisualScale && a.Scale == b.Scale && across.X != 0.0f && across.Y != 0.0f)
        {
            Vector2 shifted = (b.Shift - a.Shift) * across, offset = (b.VisualOffset - a.VisualOffset) * b.Scale, moved = b.Origin - a.Origin;
            Vector2 relaid = (b.Laid - a.Laid) * across;
            if ((shifted + offset + moved + relaid).Length() > JUMP)
                return b;

            return b with
            {
                Shift = b.Shift - Vector2.Round(shifted * (1.0f - t)) / across,
                Laid = b.Laid - Vector2.Round(relaid * (1.0f - t)) / across,
                Opacity = opacity,
                VisualOffset = b.VisualOffset - Vector2.Round(offset * (1.0f - t)) / b.Scale,
                Origin = b.Origin - Vector2.Round(moved * (1.0f - t))
            };
        }

        return b with
        {
            Shift = Vector2.Lerp(a.Shift, b.Shift, t),
            Laid = Vector2.Lerp(a.Laid, b.Laid, t),
            Opacity = opacity,
            VisualOffset = Vector2.Lerp(a.VisualOffset, b.VisualOffset, t),
            VisualScale = Vector2.Lerp(a.VisualScale, b.VisualScale, t),
            Scale = Vector2.Lerp(a.Scale, b.Scale, t),
            Origin = Vector2.Lerp(a.Origin, b.Origin, t)
        };
    }

    /// <summary>
    /// Helper method to put the frames that go where the pointer goes, and everything inside of them, where it is now.
    /// They're the newer tick's moved by whole pixels from there, never halfway between two ticks, the pointer is ahead
    /// of both.
    /// </summary>
    private void Follow(UIFrameList b, Vector2 pointer)
    {
        if (followed.Length < b.Frames.Count)
            followed = new Vector2[(int)BitOperations.RoundUpToPowerOf2((uint)b.Frames.Count)];

        // Parents come before what's inside of them, so a frame's parent is worked out by the time it is
        for (int i = 0; i < b.Frames.Count; i++)
        {
            UIFrame frame = b.Frames[i];
            bool inside = frame.Parent >= 0 && frame.Parent < i && float.IsFinite(followed[frame.Parent].X);
            followed[i] = frame.Follows ? Vector2.Round(pointer - frame.FollowFrom) : inside ? followed[frame.Parent] : new Vector2(float.NaN);
            if (float.IsFinite(followed[i].X))
                between[i] = frame with { Origin = frame.Origin + followed[i] };
        }
    }
}
