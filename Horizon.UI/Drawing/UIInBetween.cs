using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Rendering.Spriting;

namespace Horizon.UI.Drawing;

/// <summary>
/// What the UI looks like at a moment between two ticks, from the lists the two ticks painted. The compositor shows a
/// frame through this, and the tests ask it where things are drawn halfway between two ticks without a GPU. One of
/// these keeps its buffers from one frame to the next, so blending makes no garbage.
/// </summary>
internal sealed class UIInBetween
{
    private SpriteItem[] blended = [];
    private readonly List<UIGlassPane> blendedGlass = [];

    // Where every quad of the older list is, by who painted it as which of its quads, for lists that aren't the same quads
    private readonly Dictionary<Vector2, int> byKey = [];

    // The frames between two ticks and what they resolve to, and where every frame of the older tick is by its key
    private readonly List<UIFrame> between = [];
    private readonly Dictionary<Vector2, int> framesBefore = [];
    private SpriteItem[] resolved = [];
    private readonly List<UIDrawList.Run> resolvedRuns = [];
    private readonly List<UIGlassPane> resolvedGlass = [];

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
    public void Frames(UIFrameList a, UIFrameList b, float alpha)
    {
        framesBefore.Clear();
        for (int i = 0; i < a.Frames.Count; i++)
            framesBefore.TryAdd(a.Frames[i].Key, i);

        between.Clear();
        foreach (UIFrame frame in b.Frames)
            between.Add(framesBefore.TryGetValue(frame.Key, out int was) ? Between(a.Frames[was], frame, alpha) : frame);

        resolvedCount = b.Resolve(CollectionsMarshal.AsSpan(between), ref resolved, resolvedRuns, resolvedGlass);
    }

    private int resolvedCount;

    public ReadOnlySpan<SpriteItem> FramedQuads => resolved.AsSpan(0, resolvedCount);
    public ReadOnlySpan<UIDrawList.Run> FramedRuns => CollectionsMarshal.AsSpan(resolvedRuns);
    public ReadOnlySpan<UIGlassPane> FramedGlass => CollectionsMarshal.AsSpan(resolvedGlass);

    /// <summary>
    /// Helper method for a frame between where two ticks had it. One that only moved (nothing in it grew) moves by whole
    /// pixels from where the newer tick has it, see <see cref="OnPixels"/>, and one that jumped further than
    /// <see cref="JUMP"/> is where the newer tick put it.
    /// </summary>
    private static UIFrame Between(in UIFrame a, in UIFrame b, float t)
    {
        Vector2 across = b.VisualScale * b.Scale;
        if (a.VisualScale == b.VisualScale && a.Scale == b.Scale && across.X != 0.0f && across.Y != 0.0f)
        {
            Vector2 shifted = (b.Shift - a.Shift) * across, offset = (b.VisualOffset - a.VisualOffset) * b.Scale, moved = b.Origin - a.Origin;
            if ((shifted + offset + moved).Length() > JUMP)
                return b;

            return b with
            {
                Shift = b.Shift - Vector2.Round(shifted * (1.0f - t)) / across,
                VisualOffset = b.VisualOffset - Vector2.Round(offset * (1.0f - t)) / b.Scale,
                Origin = b.Origin - Vector2.Round(moved * (1.0f - t))
            };
        }

        return b with
        {
            Shift = Vector2.Lerp(a.Shift, b.Shift, t),
            VisualOffset = Vector2.Lerp(a.VisualOffset, b.VisualOffset, t),
            VisualScale = Vector2.Lerp(a.VisualScale, b.VisualScale, t),
            Scale = Vector2.Lerp(a.Scale, b.Scale, t),
            Origin = Vector2.Lerp(a.Origin, b.Origin, t)
        };
    }

    /// <summary>
    /// The quads of a frame between two ticks, the older list's blended into the newer one's by <paramref name="alpha"/>
    /// (0 is the older tick, 1 the newer). Two lists of the same quads are blended one with the other as they come. Two
    /// that aren't (a number in a corner changed, something lit up under the pointer) are blended quad by quad wherever
    /// the newer one has a quad the older one had, by who painted it, and the rest is shown as it is now. Good until
    /// the next call.
    /// </summary>
    public ReadOnlySpan<SpriteItem> Quads(ReadOnlySpan<SpriteItem> a, ReadOnlySpan<UIDrawList.Run> aRuns, ReadOnlySpan<SpriteItem> b, ReadOnlySpan<UIDrawList.Run> bRuns, float alpha)
    {
        if (blended.Length < b.Length)
            blended = new SpriteItem[(int)BitOperations.RoundUpToPowerOf2((uint)b.Length)];

        if (!Blendable(a, aRuns, b, bRuns))
        {
            BlendByKey(a, b, alpha);
        }
        else for (int i = 0; i < b.Length; i++)
        {
            // The same quad if it shows the same thing the same way, then only where it is, how big and what colour
            // moved. One that is suddenly somewhere else entirely (a menu that was folded away off screen and
            // popped up) is not shown flying over, it was put there, see SpriteItem.CanBlend
            blended[i] = a[i].Flags == b[i].Flags && a[i].TexMin == b[i].TexMin && a[i].TexMax == b[i].TexMax && SpriteItem.CanBlend(a[i], b[i])
                ? OnPixels(a[i], b[i], alpha)
                : b[i];
        }

        return blended.AsSpan(0, b.Length);
    }

    /// <summary>
    /// The panes of glass of a frame between two ticks. Blended one with the other when the two lists have as many, so a
    /// dialog that slides in has its glass slide with it, and the newer list's as they are otherwise. Good until the
    /// next call.
    /// </summary>
    public ReadOnlySpan<UIGlassPane> Glass(ReadOnlySpan<UIGlassPane> a, ReadOnlySpan<UIGlassPane> b, float alpha)
    {
        if (a.Length != b.Length)
            return b;

        blendedGlass.Clear();
        for (int i = 0; i < b.Length; i++)
        {
            // Moved by whole pixels like the quads over it, see OnPixels
            UIGlassPane pane = UIGlassPane.Blend(a[i], b[i], alpha);
            if (a[i].Max - a[i].Min == b[i].Max - b[i].Min)
            {
                Vector2 moved = Vector2.Round(pane.Min - b[i].Min);
                pane = pane with { Min = b[i].Min + moved, Max = b[i].Max + moved };
            }
            blendedGlass.Add(pane);
        }

        return CollectionsMarshal.AsSpan(blendedGlass);
    }

    /// <summary>
    /// Helper method for a quad between two ticks. One that only moved is moved from where the newer tick has it by a
    /// whole number of pixels (a screen unit is a pixel, the way the text and the minimap go by too), so it keeps its
    /// place between the pixels and everything that moved with it stays where it was against it. Moved by the fraction
    /// of the way between the ticks it came out on another place between the pixels every frame, and a scroll
    /// shimmered and wobbled. One that grew or turned on the way is blended as it is.
    /// </summary>
    private static SpriteItem OnPixels(in SpriteItem a, in SpriteItem b, float alpha)
    {
        SpriteItem between = SpriteItem.Blend(a, b, alpha);
        if (a.AxisX == b.AxisX && a.AxisY == b.AxisY)
            between.Origin = b.Origin + Vector2.Round(between.Origin - b.Origin);
        return between;
    }

    /// <summary>
    /// Helper method to blend the quads of the newer list with the ones of the older list the same component painted
    /// as the same one of its quads, wherever the older list has it. What is new is shown as it is.
    /// </summary>
    private void BlendByKey(ReadOnlySpan<SpriteItem> a, ReadOnlySpan<SpriteItem> b, float alpha)
    {
        byKey.Clear();
        for (int i = 0; i < a.Length; i++)
            byKey.TryAdd(a[i].Key, i);

        for (int i = 0; i < b.Length; i++)
        {
            blended[i] = byKey.TryGetValue(b[i].Key, out int was) &&
                a[was].Flags == b[i].Flags && a[was].TexMin == b[i].TexMin && a[was].TexMax == b[i].TexMax && SpriteItem.CanBlend(a[was], b[i])
                    ? OnPixels(a[was], b[i], alpha)
                    : b[i];
        }
    }

    /// <summary>
    /// Helper method to say whether two lists are the same quads (the same many, in the same runs, every one painted
    /// by the same component as the same one of its quads) so that they can be blended one quad with the other.
    /// Blending by place in the list alone was how a hover that put one quad in or took one out had every quad
    /// after it blended with its neighbour, a button's background halfway to the next one's, a bar across the
    /// row for a frame. A list that isn't the same quads is shown as it is, the step is a tick long and nobody sees it.
    /// </summary>
    private static bool Blendable(ReadOnlySpan<SpriteItem> a, ReadOnlySpan<UIDrawList.Run> aRuns, ReadOnlySpan<SpriteItem> b, ReadOnlySpan<UIDrawList.Run> bRuns)
    {
        if (a.Length != b.Length || !aRuns.SequenceEqual(bRuns))
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Key != b[i].Key) return false;
        }

        return true;
    }
}
