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
    private UIFrameList? cachedOlder;
    private long cachedOlderStamp = -1;
    private SpriteItem[] resolved = [];
    private readonly List<UIDrawList.Run> resolvedRuns = [];
    private readonly List<UIGlassPane> resolvedGlass = [];
    private int resolvedCount;

    /// <summary>How many times the older-tick frame index map was rebuilt, for tests.</summary>
    internal int FrameMapRebuilds { get; private set; }

    /// <summary>How many times a stationary frame bypassed per-quad frame resolution, for tests.</summary>
    internal int StationaryBypasses { get; private set; }

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
    /// <param name="paintedItems">Optional painted <see cref="SpriteItem"/> buffer for <paramref name="b"/> so stationary frames can be copied directly.</param>
    public void Frames(
        UIFrameList a,
        UIFrameList b,
        float alpha,
        Vector2? pointer = null,
        ReadOnlySpan<SpriteItem> paintedItems = default,
        ReadOnlySpan<UIDrawList.Run> paintedRuns = default,
        ReadOnlySpan<UIGlassPane> paintedGlass = default,
        bool hasPaintedGlass = false)
    {
        bool following = pointer is not null && b.Follows;

        // When alpha has reached the newer tick and no frame follows the pointer, every frame is stationary.
        if (alpha >= 1.0f && !following && TryBypassStationary(b, paintedItems, paintedRuns, paintedGlass, hasPaintedGlass))
            return;

        ReadOnlySpan<UIFrame> older = a.Span, newer = b.Span;
        if (alpha < 1.0f && (!ReferenceEquals(a, cachedOlder) || a.Stamp != cachedOlderStamp))
        {
            framesBefore.Clear();
            for (int i = 0; i < older.Length; i++)
                framesBefore.TryAdd(older[i].Key, i);
            cachedOlder = a;
            cachedOlderStamp = a.Stamp;
            FrameMapRebuilds++;
        }

        between.Clear();
        bool allStationary = true;
        for (int i = 0; i < newer.Length; i++)
        {
            ref readonly UIFrame frame = ref newer[i];
            UIFrame blended = alpha < 1.0f && framesBefore.TryGetValue(frame.Key, out int was)
                ? Between(older[was], frame, alpha)
                : frame;
            if (allStationary && !FrameMatches(blended, frame))
                allStationary = false;
            between.Add(blended);
        }

        if (following && Follow(b, pointer!.Value))
            allStationary = false;

        if (allStationary && TryBypassStationary(b, paintedItems, paintedRuns, paintedGlass, hasPaintedGlass))
            return;

        resolvedCount = b.Resolve(CollectionsMarshal.AsSpan(between), ref resolved, resolvedRuns, resolvedGlass, paintedItems);
    }

    // Helper method to copy painted quads, runs, and glass directly when every frame in b is unchanged from how it was painted.
    private bool TryBypassStationary(
        UIFrameList b,
        ReadOnlySpan<SpriteItem> paintedItems,
        ReadOnlySpan<UIDrawList.Run> paintedRuns,
        ReadOnlySpan<UIGlassPane> paintedGlass,
        bool hasPaintedGlass)
    {
        if (b.QuadCount == 0 && paintedItems.IsEmpty)
        {
            if (hasPaintedGlass)
            {
                resolvedGlass.Clear();
                if (!paintedGlass.IsEmpty)
                    resolvedGlass.AddRange(paintedGlass);
            }
            else if (!b.ResolveStationaryGlass(resolvedGlass))
            {
                return false;
            }

            resolvedCount = 0;
            resolvedRuns.Clear();
            StationaryBypasses++;
            return true;
        }

        // Caller did not supply paintedItems (e.g. full-resolve parity tests); run full Resolve.
        if (paintedItems.IsEmpty && paintedRuns.IsEmpty && !hasPaintedGlass)
            return false;

        // Populate runs from paintedRuns if supplied, or directly from b.Runs using recorded ItemEnd boundaries.
        resolvedRuns.Clear();
        if (!paintedRuns.IsEmpty)
        {
            resolvedRuns.AddRange(paintedRuns);
        }
        else if (!paintedItems.IsEmpty)
        {
            ReadOnlySpan<UIFrameRun> runSpan = CollectionsMarshal.AsSpan(b.Runs);
            if (runSpan.IsEmpty)
                return false;

            int runFirst = 0;
            for (int i = 0; i < runSpan.Length; i++)
            {
                ref readonly UIFrameRun fr = ref runSpan[i];
                int itemEnd = fr.ItemEnd >= 0
                    ? fr.ItemEnd
                    : (runSpan.Length == 1 && fr.End == b.QuadCount ? paintedItems.Length : -1);
                if (itemEnd < runFirst || itemEnd > paintedItems.Length)
                    return false;
                if (itemEnd > runFirst)
                    resolvedRuns.Add(new UIDrawList.Run(runFirst, itemEnd - runFirst, fr.Image0, fr.Image1));
                runFirst = itemEnd;
            }
            if (runFirst != paintedItems.Length)
                return false;
        }

        // Populate glass panes from paintedGlass if supplied, or resolve only the glass panes without touching quads.
        if (hasPaintedGlass)
        {
            resolvedGlass.Clear();
            if (!paintedGlass.IsEmpty)
                resolvedGlass.AddRange(paintedGlass);
        }
        else if (!b.ResolveStationaryGlass(resolvedGlass))
        {
            return false;
        }

        if (!paintedItems.IsEmpty)
        {
            if (resolved.Length < paintedItems.Length)
                resolved = new SpriteItem[(int)BitOperations.RoundUpToPowerOf2((uint)paintedItems.Length)];
            paintedItems.CopyTo(resolved);
        }
        resolvedCount = paintedItems.Length;
        StationaryBypasses++;
        return true;
    }

    private static bool FrameMatches(in UIFrame a, in UIFrame b) =>
        a.Shift == b.Shift &&
        a.Laid == b.Laid &&
        a.VisualScale == b.VisualScale &&
        a.VisualOffset == b.VisualOffset &&
        a.Scale == b.Scale &&
        a.Origin == b.Origin &&
        a.Opacity == b.Opacity;

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
    internal static UIFrame Between(in UIFrame older, in UIFrame b, float t)
    {
        if (t >= 1.0f || FrameMatches(older, b))
            return b;

        // A frame whose shift jumped this tick (a zoom keeping its place, see UIFrame.Jumped) has none of it to move by,
        // what's in it was laid out for the new shift, and only the rest of it is blended
        UIFrame a = b.Jumped ? older with { Shift = b.Shift, Laid = b.Laid } : older;
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
    private bool Follow(UIFrameList b, Vector2 pointer)
    {
        if (followed.Length < b.Frames.Count)
            followed = new Vector2[(int)BitOperations.RoundUpToPowerOf2((uint)b.Frames.Count)];

        // Parents come before what's inside of them, so a frame's parent is worked out by the time it is
        bool moved = false;
        ReadOnlySpan<UIFrame> frames = b.Span;
        for (int i = 0; i < frames.Length; i++)
        {
            ref readonly UIFrame frame = ref frames[i];
            bool inside = frame.Parent >= 0 && frame.Parent < i && float.IsFinite(followed[frame.Parent].X);
            followed[i] = frame.Follows ? Vector2.Round(pointer - frame.FollowFrom) : inside ? followed[frame.Parent] : new Vector2(float.NaN);
            if (float.IsFinite(followed[i].X))
            {
                between[i] = frame with { Origin = frame.Origin + followed[i] };
                if (followed[i] != Vector2.Zero)
                    moved = true;
            }
        }

        return moved;
    }
}
