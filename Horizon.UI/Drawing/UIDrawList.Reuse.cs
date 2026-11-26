using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Logging;
using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

// A component that says what it paints only changes with its PaintVersion (see UIComponent.PaintVersion) is painted
// once and then, tick after tick while the version and where it's painted stay the same, taken out of the list painted
// the tick before as it was, its quads, its frames, its clips and all. Only the code editor and the like are worth it,
// they paint thousands of quads out of a lot of state
public sealed partial class UIDrawList
{
    /// <summary>
    /// The list painted the tick before this one, which what's painted the same again is taken out of. Set by whoever
    /// paints into two lists in turn, null for none.
    /// </summary>
    internal UIDrawList? Previous { get; set; }

    /// <summary>
    /// Whether a component that could be taken out of the list before is painted anyway and held up against what it
    /// would have been, a difference logged, <c>HORIZON_UI_REUSE_CHECK=1</c>. For finding a PaintVersion that leaves
    /// something out. Costs every paint it would have saved.
    /// </summary>
    internal static bool CheckReuse { get; set; } = Environment.GetEnvironmentVariable("HORIZON_UI_REUSE_CHECK") == "1";

    // A contiguous slice of items and frame quads within a recorded component that shares one run's custom image slots,
    // holding only the textures actually referenced by that slice (never inherited from a previous sibling).
    private readonly record struct RecordedRun(int Items, int ItemsEnd, int Quads, int QuadsEnd, Texture? Image0, Texture? Image1);

    // What a component painted, where it is among the list's quads, frame quads, frames, clips, homes, glass panes,
    // and recorded run segments, by the component's name
    private readonly record struct PaintRecord(
        long Version, int Context, int Items, int ItemsEnd, int Quads, int QuadsEnd, int Frames, int FramesEnd,
        int Clips, int ClipsEnd, int Homes, int HomesEnd, int Glass, int GlassEnd, int FrameGlass, int FrameGlassEnd,
        int Runs, int RunsEnd);

    private readonly Dictionary<float, PaintRecord> records = [];
    private readonly List<RecordedRun> recordedRuns = [];

    /// <summary>How many components were taken out of the list before this paint instead of painted, for the tests.</summary>
    internal int Reused { get; private set; }

    // Where a recording started, and how many things that can't be painted again had happened by then
    private readonly Stack<(int Items, int Quads, int Frames, int Clips, int Homes, int Glass, int FrameGlass, int FrameRuns, int Uncacheable)> recording = new();
    private int uncacheable;

    // The components that started painting in frames, in order, for a record to carry its own
    private readonly List<(float PaintId, int Frame)> homeOrder = [];

    // Helper method to start the records over with the list
    private void BeginRecords()
    {
        records.Clear();
        recordedRuns.Clear();
        recording.Clear();
        Reused = 0;
        homeOrder.Clear();
        uncacheable = 0;
    }

    /// <summary>
    /// Says that what's painted now can't be taken out of this list again as it was. A run of images that
    /// closed, a component raised over the rest, an icon that moves.
    /// </summary>
    internal void Uncacheable() => uncacheable++;

    /// <summary>Where a component is painted, as a number that's another whenever any of it is, see <see cref="TryReuse"/>.</summary>
    // The skin's colours as one number, taken as the paint began
    private int paletteFingerprint;

    internal int PaintContext(UIRect bounds)
    {
        var hash = new HashCode();
        hash.Add(bounds);
        hash.Add(clip);
        hash.Add(visualOffset);
        hash.Add(visualScale);
        hash.Add(scale);
        hash.Add(origin);
        hash.Add(opacity);
        hash.Add(shift);
        hash.Add(laid);
        hash.Add(Skin);
        hash.Add(UISkins.Version);
        hash.Add(paletteFingerprint);
        hash.Add(font);
        // Letters kept from before the glyph atlas was emptied point at pictures that aren't there any more
        hash.Add(Horizon.Rendering.Text.GlyphAtlas.Shared.Generation);
        hash.Add(Horizon.Rendering.Text.GlyphAtlas.Subpixel.Generation);
        // And letters smoothed another way, or kept from before the subpixel atlas was there
        hash.Add(subpixelText && Horizon.Rendering.Text.GlyphAtlas.Subpixel.Ready);
        hash.Add(subpixelBgr);
        hash.Add(hintedText);
        if (fades.Count > 0)
        {
            foreach (ref readonly var fade in CollectionsMarshal.AsSpan(fades))
            {
                hash.Add(fade.Area);
                hash.Add(fade.Top);
                hash.Add(fade.Bottom);
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Paints a component the way it was painted the tick before, out of the list painted then, if it was painted at
    /// the same version in the same place. False if it has to be painted.
    /// </summary>
    internal bool TryReuse(float paintId, long version, int context)
    {
        if (Previous is not { } before || !before.records.TryGetValue(paintId, out PaintRecord was) || was.Version != version || was.Context != context)
            return false;

        if (CheckReuse)
            return false;

        Reuse(before, paintId, was);
        Reused++;
        return true;
    }

    /// <summary>Starts recording what a component paints, for the next tick to take it out of this list again.</summary>
    internal void BeginRecord() =>
        recording.Push((itemCount, frameList.QuadCount, frameList.Frames.Count, frameList.Clips.Count, homeOrder.Count, glass.Count, frameList.Glass.Count, frameList.Runs.Count, uncacheable));

    /// <summary>Ends what <see cref="BeginRecord"/> started, kept unless something that can't be painted again was painted.</summary>
    /// <param name="painter">Who it is, said when CheckReuse finds a difference.</param>
    internal void EndRecord(float paintId, long version, int context, object painter)
    {
        var (items0, quads0, frames0, clips0, homes0, glass0, frameGlass0, frameRuns0, uncacheable0) = recording.Pop();
        bool kept = uncacheable == uncacheable0;
        if (CheckReuse && Previous is { } before && before.records.TryGetValue(paintId, out PaintRecord was) && was.Version == version && was.Context == context
            && Differs(before, was, items0, quads0, glass0) is { } differs)
            Log.Warning($"[UI reuse] a {painter.GetType().Name} painted something else at the same PaintVersion, {differs}");

        if (kept)
        {
            int segStart = recordedRuns.Count;
            RecordRunSegments(items0, itemCount, quads0, frameList.QuadCount, frameRuns0, frameList.Runs.Count);
            records[paintId] = new PaintRecord(version, context, items0, itemCount, quads0, frameList.QuadCount, frames0, frameList.Frames.Count,
                clips0, frameList.Clips.Count, homes0, homeOrder.Count, glass0, glass.Count, frameGlass0, frameList.Glass.Count,
                segStart, recordedRuns.Count);
        }
    }

    // Helper method to split a recorded slice along any run boundaries closed during its paint, keeping only the custom
    // images actually referenced by quads inside each segment so sibling textures are never inherited.
    private void RecordRunSegments(int items0, int itemsEnd, int quads0, int quadsEnd, int frameRuns0, int frameRunsEnd)
    {
        int segItems = items0;
        int segQuads = quads0;

        for (int r = frameRuns0; r < frameRunsEnd; r++)
        {
            UIFrameRun fr = frameList.Runs[r];
            int qEnd = Math.Clamp(fr.End, segQuads, quadsEnd);
            if (qEnd <= segQuads)
                continue;

            int iEnd = segItems;
            bool used0 = false, used1 = false;
            for (int q = segQuads; q < qEnd; q++)
            {
                ref readonly UIFrameQuad fq = ref frameList.Quads[q];
                if (fq.Item >= 0)
                    iEnd = fq.Item + 1;
                uint tex = SpriteItem.TextureOf(fq.Flags);
                if (tex == IMAGE_SLOT) used0 = true;
                else if (tex == IMAGE_SLOT + 1) used1 = true;
            }

            recordedRuns.Add(new RecordedRun(segItems, iEnd, segQuads, qEnd, used0 ? fr.Image0 : null, used1 ? fr.Image1 : null));
            segItems = iEnd;
            segQuads = qEnd;
        }

        if (quadsEnd > segQuads)
        {
            bool used0 = false, used1 = false;
            for (int q = segQuads; q < quadsEnd; q++)
            {
                uint tex = SpriteItem.TextureOf(frameList.Quads[q].Flags);
                if (tex == IMAGE_SLOT) used0 = true;
                else if (tex == IMAGE_SLOT + 1) used1 = true;
            }

            recordedRuns.Add(new RecordedRun(segItems, itemsEnd, segQuads, quadsEnd, used0 ? runImage0 : null, used1 ? runImage1 : null));
        }
    }

    // Helper method to copy what a component painted the tick before into this list, where it's painted now
    private void Reuse(UIDrawList before, float paintId, PaintRecord was)
    {
        int items0 = itemCount, quads0 = frameList.QuadCount, frames0 = frameList.Frames.Count, clips0 = frameList.Clips.Count;
        int glass0 = glass.Count, frameGlass0 = frameList.Glass.Count, runsStart = recordedRuns.Count;

        // Frames and clips inside of it numbered again among this list's, and the ones it was inside of are the ones
        // it's inside of now
        int frameCount = was.FramesEnd - was.Frames;
        int frameDelta = frames0 - was.Frames;
        if (frameCount > 0)
        {
            frameList.Frames.EnsureCapacity(frames0 + frameCount);
            for (int i = was.Frames; i < was.FramesEnd; i++)
            {
                UIFrame frame = before.frameList.Frames[i];
                int parent = frame.Parent >= was.Frames && frame.Parent < was.FramesEnd ? frame.Parent + frameDelta : frameAt;
                frameList.Frames.Add(frame with { Parent = parent });
            }
        }

        int clipCount = was.ClipsEnd - was.Clips;
        int clipDelta = clips0 - was.Clips;
        if (clipCount > 0)
        {
            frameList.Clips.EnsureCapacity(clips0 + clipCount);
            for (int i = was.Clips; i < was.ClipsEnd; i++)
            {
                UIFrameClip cut = before.frameList.Clips[i];
                int cutFrame = cut.Frame >= was.Frames && cut.Frame < was.FramesEnd ? cut.Frame + frameDelta : frameAt;
                int cutParent = cut.Parent >= was.Clips && cut.Parent < was.ClipsEnd ? cut.Parent + clipDelta : clipAt;
                int? cutStationary = cut.StationaryParentFrame is { } outside
                    ? (outside >= was.Frames && outside < was.FramesEnd ? outside + frameDelta : frameAt)
                    : null;
                frameList.Clips.Add(cut with { Frame = cutFrame, Parent = cutParent, StationaryParentFrame = cutStationary });
            }
        }

        int totalItems = was.ItemsEnd - was.Items;
        if (totalItems > 0 && itemCount + totalItems > items.Length)
        {
            int next = items.Length;
            while (next < itemCount + totalItems)
                next *= 2;
            Array.Resize(ref items, next);
        }

        int totalQuads = was.QuadsEnd - was.Quads;
        if (totalQuads > 0)
            frameList.EnsureCapacity(quads0 + totalQuads);

        int itemDelta = items0 - was.Items;
        bool hasInnerFrames = frameCount > 0;
        bool hasInnerClips = clipCount > 0;

        // Replay each recorded run segment in order, closing the active run before a segment whose images do not fit
        // into the remaining slots so multi-run boundaries and active images stay exact.
        for (int r = was.Runs; r < was.RunsEnd; r++)
        {
            RecordedRun seg = before.recordedRuns[r];
            Texture? img0 = seg.Image0;
            Texture? img1 = seg.Image1;

            int needed = 0;
            if (img0 is not null && !ReferenceEquals(img0, runImage0) && !ReferenceEquals(img0, runImage1))
                needed++;
            if (img1 is not null && !ReferenceEquals(img1, img0) && !ReferenceEquals(img1, runImage0) && !ReferenceEquals(img1, runImage1))
                needed++;
            int available = (runImage0 is null ? 1 : 0) + (runImage1 is null ? 1 : 0);
            if (needed > available)
                CloseRun();

            uint slot0 = img0 is not null ? ImageSlot(img0) : 0;
            uint slot1 = img1 is not null ? ImageSlot(img1) : 0;
            bool remapSlots = (img0 is not null && slot0 != IMAGE_SLOT) || (img1 is not null && slot1 != IMAGE_SLOT + 1);

            int segDstItems = itemCount;
            int segItemCount = seg.ItemsEnd - seg.Items;
            if (segItemCount > 0)
            {
                before.items.AsSpan(seg.Items, segItemCount).CopyTo(items.AsSpan(segDstItems));
                // Most idle ticks bind the same images into the same slots, so the copied quads need no per-item flag rewrite.
                if (remapSlots)
                {
                    for (int i = segDstItems; i < segDstItems + segItemCount; i++)
                        items[i].Flags = RemapImageSlot(items[i].Flags, slot0, slot1);
                }
                itemCount += segItemCount;
            }

            int segDstQuads = frameList.QuadCount;
            int segQuadCount = seg.QuadsEnd - seg.Quads;
            if (segQuadCount > 0)
            {
                before.frameList.Quads.AsSpan(seg.Quads, segQuadCount).CopyTo(frameList.Quads.AsSpan(segDstQuads, segQuadCount));
                frameList.QuadCount = segDstQuads + segQuadCount;

                Span<UIFrameQuad> dst = frameList.Quads.AsSpan(segDstQuads, segQuadCount);
                for (int i = 0; i < segQuadCount; i++)
                {
                    ref UIFrameQuad quad = ref dst[i];
                    quad.Frame = hasInnerFrames && quad.Frame >= was.Frames && quad.Frame < was.FramesEnd ? quad.Frame + frameDelta : frameAt;
                    quad.Clip = hasInnerClips && quad.Clip >= was.Clips && quad.Clip < was.ClipsEnd ? quad.Clip + clipDelta : clipAt;
                    if (quad.Item >= 0)
                        quad.Item += itemDelta;
                    if (remapSlots)
                        quad.Flags = RemapImageSlot(quad.Flags, slot0, slot1);
                }
            }

            Texture? newImg0 = slot0 == IMAGE_SLOT ? img0 : slot1 == IMAGE_SLOT ? img1 : null;
            Texture? newImg1 = slot0 == IMAGE_SLOT + 1 ? img0 : slot1 == IMAGE_SLOT + 1 ? img1 : null;
            recordedRuns.Add(new RecordedRun(segDstItems, itemCount, segDstQuads, frameList.QuadCount, newImg0, newImg1));
        }

        int glassCount = was.GlassEnd - was.Glass;
        if (glassCount > 0)
        {
            glass.EnsureCapacity(glass0 + glassCount);
            for (int i = was.Glass; i < was.GlassEnd; i++)
            {
                UIGlassPane pane = before.glass[i];
                glass.Add(pane with { Under = pane.Under + itemDelta, Start = pane.Start + itemDelta });
            }
        }

        int frameGlassCount = was.FrameGlassEnd - was.FrameGlass;
        if (frameGlassCount > 0)
        {
            frameList.Glass.EnsureCapacity(frameGlass0 + frameGlassCount);
            int quadDelta = quads0 - was.Quads;
            for (int i = was.FrameGlass; i < was.FrameGlassEnd; i++)
            {
                UIFrameGlass fg = before.frameList.Glass[i];
                int fgFrame = fg.Frame >= was.Frames && fg.Frame < was.FramesEnd ? fg.Frame + frameDelta : frameAt;
                int fgClip = fg.Clip >= was.Clips && fg.Clip < was.ClipsEnd ? fg.Clip + clipDelta : clipAt;
                frameList.Glass.Add(fg with
                {
                    Under = fg.Under + quadDelta,
                    Start = fg.Start + quadDelta,
                    Frame = fgFrame,
                    Clip = fgClip
                });
            }
        }

        int homeCount = was.HomesEnd - was.Homes;
        if (homeCount > 0)
        {
            homeOrder.EnsureCapacity(homeOrder.Count + homeCount);
            for (int i = was.Homes; i < was.HomesEnd; i++)
            {
                var (id, frame) = before.homeOrder[i];
                int home = frame >= was.Frames && frame < was.FramesEnd ? frame + frameDelta : frameAt;
                homeOrder.Add((id, home));
                homes.TryAdd(id, home);
            }
        }

        records[paintId] = was with
        {
            Items = items0, ItemsEnd = itemCount, Quads = quads0, QuadsEnd = frameList.QuadCount,
            Frames = frames0, FramesEnd = frameList.Frames.Count, Clips = clips0, ClipsEnd = frameList.Clips.Count,
            Homes = homeOrder.Count - homeCount, HomesEnd = homeOrder.Count,
            Glass = glass0, GlassEnd = glass.Count, FrameGlass = frameGlass0, FrameGlassEnd = frameList.Glass.Count,
            Runs = runsStart, RunsEnd = recordedRuns.Count
        };
    }

    private static uint RemapImageSlot(uint flags, uint slot0, uint slot1)
    {
        uint texture = SpriteItem.TextureOf(flags);
        return texture == IMAGE_SLOT ? SpriteItem.WithTexture(flags, slot0)
            : texture == IMAGE_SLOT + 1 ? SpriteItem.WithTexture(flags, slot1)
            : flags;
    }

    // Helper method for where what a component painted now differs from what it painted at the same version before, for CheckReuse
    private string? Differs(UIDrawList before, in PaintRecord was, int items0, int quads0, int glass0)
    {
        int count = itemCount - items0;
        if (count != was.ItemsEnd - was.Items)
            return $"{count} quads painted, {was.ItemsEnd - was.Items} before";

        ReadOnlySpan<SpriteItem> now = items.AsSpan(items0, count), then = before.items.AsSpan(was.Items, count);
        for (int i = 0; i < count; i++)
        {
            SpriteItem a = now[i], b = then[i];
            // An image's slot can be another in another run, the rest of the flags are what they are
            uint texture = SpriteItem.TextureOf(a.Flags);
            bool image = texture == IMAGE_SLOT || texture == IMAGE_SLOT + 1;
            if (a.Origin != b.Origin || a.AxisX != b.AxisX || a.AxisY != b.AxisY || a.TexMin != b.TexMin || a.TexMax != b.TexMax || a.Color != b.Color || a.Ring != b.Ring
                || !image && a.Flags != b.Flags)
                return $"quad {i} of {count} is at {a.Origin} {a.Color:X8} now and was at {b.Origin} {b.Color:X8}";
        }

        int glassCount = glass.Count - glass0;
        if (glassCount != was.GlassEnd - was.Glass)
            return $"{glassCount} glass panes painted, {was.GlassEnd - was.Glass} before";

        for (int i = 0; i < glassCount; i++)
        {
            UIGlassPane a = glass[glass0 + i], b = before.glass[was.Glass + i];
            if (a.Under - items0 != b.Under - was.Items || a.Start - items0 != b.Start - was.Items ||
                a.Min != b.Min || a.Max != b.Max || a.ClipMin != b.ClipMin || a.ClipMax != b.ClipMax ||
                a.Radius != b.Radius || a.Blur != b.Blur || a.Opacity != b.Opacity || a.Bend != b.Bend ||
                a.Lens != b.Lens || a.Tint != b.Tint)
                return $"glass pane {i} of {glassCount} is {a} now and was {b}";
        }

        return null;
    }
}
