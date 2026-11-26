using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// Draws the box of text that comes up next to the pointer when it rests on a component for a moment, see
/// <see cref="UIComponent.Tooltip"/>. The compositor decides when, this only draws it, a box in the colours of the
/// skin, just below and to the right of the pointer, pushed back onto the screen where it would hang off it.
/// </summary>
internal static class UITooltip
{
    // How wide a tooltip is at most before its text wraps, and the room around the text
    private const float MAX_WIDTH = 320.0f;
    private const float MAX_RICH_WIDTH = 520.0f;
    private const float PADDING = 12.0f;

    // How far from the pointer the box sits, so the pointer doesn't cover its first word, how far over the pointer its
    // bottom is when it goes over it, and how far in from the edges of the window it stays
    private static readonly Vector2 Offset = new(14.0f, -22.0f);
    private const float ABOVE = 24.0f, MARGIN = 16.0f;

    // How wide the widest word of a text is
    private static float LongestWord(UISkin skin, string text, float scale)
    {
        float widest = 0.0f;
        // By spans, it's asked every frame a tooltip is up and a Split made garbage each time
        foreach (Range range in text.AsSpan().SplitAny(' ', '\n'))
        {
            ReadOnlySpan<char> word = text.AsSpan(range);
            if (!word.IsEmpty)
                widest = MathF.Max(widest, skin.Font.Measure(word, scale, markup: false).X);
        }
        return widest;
    }

    // Small fixed LRU cache of plain tooltip layouts so alternating hovered items reuse wrapped lines and size without
    // per-frame heap allocations or repeated binary-search wrapping.
    private const int CACHE_SIZE = 8;

    private struct TooltipCacheEntry
    {
        public UIFont? Font;
        public string? Text;
        public float Scale;
        public float MaxFrameWidth;
        public Vector2 MeasuredSize;
        public (int Start, int Length)[]? Lines;
        public int LineCount;
        public long Stamp;
    }

    private static readonly TooltipCacheEntry[] layoutCache = new TooltipCacheEntry[CACHE_SIZE];
    private static long cacheClock;

    /// <summary>Cache hit and miss counters for plain tooltip layout lookups, for unit tests.</summary>
    internal static int CacheHits { get; private set; }
    internal static int CacheMisses { get; private set; }

    /// <summary>Clears the plain tooltip layout cache and counters, for unit tests.</summary>
    internal static void ResetCache()
    {
        Array.Clear(layoutCache);
        cacheClock = 0;
        CacheHits = 0;
        CacheMisses = 0;
    }

    /// <summary>
    /// Helper method for the narrowest width a text wraps to in as few lines as it does at its widest, so the lines come
    /// out about even. A tooltip of two lines had one word left on its own on the second.
    /// </summary>
    private static float Balanced(UISkin skin, string text, float scale, float longest, float most, List<(int Start, int Length)> lines)
    {
        skin.Font.Wrap(text, scale, most, lines, markup: false);
        int count = lines.Count;
        float low = longest, high = most;
        if (count > 1)
        {
            // Halved a few times, a pixel or two out is nothing anybody sees
            for (int i = 0; i < 8 && high - low > 2.0f; i++)
            {
                float middle = (low + high) * 0.5f;
                skin.Font.Wrap(text, scale, middle, lines, markup: false);
                if (lines.Count > count)
                    low = middle;
                else
                    high = middle;
            }
        }

        return count > 1 ? high : most;
    }

    /// <summary>
    /// Resolves wrapped lines and outer tooltip box size for plain text using the fixed 8-entry LRU cache.
    /// </summary>
    internal static Vector2 ResolvePlainLayout(UISkin skin, string text, float scale, float frameAvailWidth, List<(int Start, int Length)> lines)
    {
        UIFont font = skin.Font;
        for (int i = 0; i < CACHE_SIZE; i++)
        {
            ref TooltipCacheEntry entry = ref layoutCache[i];
            if (entry.Text is not null
                && ReferenceEquals(entry.Font, font)
                && entry.Scale == scale
                && entry.MaxFrameWidth == frameAvailWidth
                && string.Equals(entry.Text, text, StringComparison.Ordinal))
            {
                entry.Stamp = ++cacheClock;
                CacheHits++;

                int count = entry.LineCount;
                lines.Clear();
                if (count > 0 && entry.Lines is { } cachedLines)
                {
                    System.Runtime.InteropServices.CollectionsMarshal.SetCount(lines, count);
                    cachedLines.AsSpan(0, count).CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines));
                }

                return entry.MeasuredSize;
            }
        }

        CacheMisses++;

        // As wide as its longest word when that's wider than a tooltip usually is, so a word is never broken in two (a
        // path came out as "RE/ADME.md"), never wider than the frame leaves
        float longest = LongestWord(skin, text, scale);
        float most = MathF.Max(MAX_WIDTH, MathF.Min(longest, frameAvailWidth));
        float width = Balanced(skin, text, scale, longest, most, lines);
        font.Wrap(text, scale, width, lines, markup: false);
        Vector2 size = font.Measure(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), scale, markup: false) + new Vector2(PADDING * 2.0f);

        int slot = 0;
        long oldest = long.MaxValue;
        for (int i = 0; i < CACHE_SIZE; i++)
        {
            if (layoutCache[i].Text is null)
            {
                slot = i;
                break;
            }
            if (layoutCache[i].Stamp < oldest)
            {
                oldest = layoutCache[i].Stamp;
                slot = i;
            }
        }

        ref TooltipCacheEntry target = ref layoutCache[slot];
        target.Font = font;
        target.Text = text;
        target.Scale = scale;
        target.MaxFrameWidth = frameAvailWidth;
        target.MeasuredSize = size;
        target.LineCount = lines.Count;
        target.Stamp = ++cacheClock;

        if (lines.Count > 0)
        {
            if (target.Lines is null || target.Lines.Length < lines.Count)
                target.Lines = new (int Start, int Length)[Math.Max(4, lines.Count)];
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines).CopyTo(target.Lines);
        }

        return size;
    }

    /// <summary>
    /// Paints a tooltip into a list that has the transform of the module set, see <see cref="UIModule.Paint"/>.
    /// </summary>
    /// <param name="point">Where the pointer is, in the module's units.</param>
    /// <param name="lines">Scratch for cutting the text into lines, reused from one frame to the next.</param>
    /// <param name="rich">Where a tooltip that is markdown is laid out, null for one that is plain words.</param>
    /// <param name="openedAt">When it was first painted, for its entrance, less than 0 for not yet.</param>
    /// <returns>The box it was painted in, in the module's units, so the pointer can go over to it and read it.</returns>
    public static UIRect Paint(UIDrawList list, UIModule module, string text, Vector2 point, List<(int Start, int Length)> lines, UIRichText? rich, ref float openedAt)
    {
        var skin = list.Skin;

        // The size of what the skin writes, a tooltip in small print was the hardest thing in the UI to read
        float scale = skin.TextScale;
        text = module.Compositor.Resolve(text);

        Vector2 size;
        if (rich is not null)
        {
            rich.Build(text, skin.Font, scale, MAX_RICH_WIDTH, markdown: true);
            size = new Vector2(rich.Width, rich.Height) + new Vector2(PADDING * 2.0f);
        }
        else
        {
            float frameAvailWidth = module.Frame.Width - MARGIN * 2.0f - PADDING * 2.0f;
            size = ResolvePlainLayout(skin, text, scale, frameAvailWidth, lines);
        }

        // Its top left corner hangs off the pointer, under it, or over it when there's no room under it (a status bar's
        // tooltip was pushed up over the item it was about). The whole box stays inside the frame of the module with a
        // margin, one flush with the window's edges lost its round corners there
        UIRect frame = module.Frame;
        Vector2 corner = point + Offset;
        if (corner.Y - size.Y < frame.Min.Y + MARGIN)
            corner.Y = point.Y + ABOVE + size.Y;
        corner.X = MathF.Min(corner.X, frame.Max.X - MARGIN - size.X);
        corner.Y = MathF.Max(corner.Y, frame.Min.Y + MARGIN + size.Y);
        corner.X = MathF.Max(corner.X, frame.Min.X + MARGIN);
        corner.Y = MathF.Min(corner.Y, frame.Max.Y - MARGIN);

        var box = new UIRect(new Vector2(corner.X, corner.Y - size.Y), new Vector2(corner.X + size.X, corner.Y));

        // Faded in out of the corner it hangs from, on what every popup sits on
        bool entering = list.BeginEntrance(ref openedAt, new Vector2(box.Min.X, box.Max.Y), new Vector2(0.0f, 4.0f));
        list.Popup(box, module.Compositor.PopupGlass);

        if (rich is not null)
            rich.Paint(list, box.Shrink(new UIEdges(PADDING)), Origin.TopLeft, skin.TextColor);
        else
            list.Text(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), box.Shrink(new UIEdges(PADDING)), Origin.TopLeft, scale, skin.TextColor, markup: false);

        if (entering)
            list.EndEntrance();

        return box;
    }
}
