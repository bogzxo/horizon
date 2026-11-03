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
                ? SpriteItem.Blend(a[i], b[i], alpha)
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
            blendedGlass.Add(UIGlassPane.Blend(a[i], b[i], alpha));

        return CollectionsMarshal.AsSpan(blendedGlass);
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
                    ? SpriteItem.Blend(a[was], b[i], alpha)
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
