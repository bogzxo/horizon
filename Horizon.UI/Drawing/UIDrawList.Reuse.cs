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

    // What a component painted, where it is among the list's quads, frame quads, frames, clips and homes, the frame
    // and clip it was painted inside of, and the images its run had, by the component's name
    private readonly record struct PaintRecord(
        long Version, int Context, int Items, int ItemsEnd, int Quads, int QuadsEnd, int Frames, int FramesEnd,
        int Clips, int ClipsEnd, int Homes, int HomesEnd, Texture? Image0, Texture? Image1);

    private readonly Dictionary<float, PaintRecord> records = [];

    /// <summary>How many components were taken out of the list before this paint instead of painted, for the tests.</summary>
    internal int Reused { get; private set; }

    // Where a recording started, and how many things that can't be painted again had happened by then
    private readonly Stack<(int Items, int Quads, int Frames, int Clips, int Homes, int Runs, int FrameRuns, int Uncacheable)> recording = new();
    private int uncacheable;

    // The components that started painting in frames, in order, for a record to carry its own
    private readonly List<(float PaintId, int Frame)> homeOrder = [];

    // Helper method to start the records over with the list
    private void BeginRecords()
    {
        records.Clear();
        recording.Clear();
        Reused = 0;
        homeOrder.Clear();
        uncacheable = 0;
    }

    /// <summary>
    /// Says that what's painted now can't be taken out of this list again as it was. Glass, a run of images that
    /// closed, a component raised over the rest, an icon that moves.
    /// </summary>
    internal void Uncacheable() => uncacheable++;

    /// <summary>Where a component is painted, as a number that's another whenever any of it is, see <see cref="TryReuse"/>.</summary>
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
        hash.Add(Skin.Palette.Fingerprint());
        hash.Add(font);
        foreach (var (area, top, bottom) in fades)
        {
            hash.Add(area);
            hash.Add(top);
            hash.Add(bottom);
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
        recording.Push((itemCount, frameList.QuadCount, frameList.Frames.Count, frameList.Clips.Count, homeOrder.Count, runs.Count, frameList.Runs.Count, uncacheable));

    /// <summary>Ends what <see cref="BeginRecord"/> started, kept unless something that can't be painted again was painted.</summary>
    /// <param name="painter">Who it is, said when CheckReuse finds a difference.</param>
    internal void EndRecord(float paintId, long version, int context, object painter)
    {
        var (items0, quads0, frames0, clips0, homes0, runs0, frameRuns0, uncacheable0) = recording.Pop();
        bool kept = uncacheable == uncacheable0 && runs.Count == runs0 && frameList.Runs.Count == frameRuns0;
        if (CheckReuse && Previous is { } before && before.records.TryGetValue(paintId, out PaintRecord was) && was.Version == version && was.Context == context
            && Differs(before, was, items0, quads0) is { } differs)
            Log.Warning($"[UI reuse] a {painter.GetType().Name} painted something else at the same PaintVersion, {differs}");

        if (kept)
            records[paintId] = new PaintRecord(version, context, items0, itemCount, quads0, frameList.QuadCount, frames0, frameList.Frames.Count,
                clips0, frameList.Clips.Count, homes0, homeOrder.Count, runImage0, runImage1);
    }

    // Helper method to copy what a component painted the tick before into this list, where it's painted now
    private void Reuse(UIDrawList before, float paintId, PaintRecord was)
    {
        // Its images in slots of this run, the slots it had then said the same images
        uint slot0 = was.Image0 is { } image0 ? ImageSlot(image0) : 0;
        uint slot1 = was.Image1 is { } image1 ? ImageSlot(image1) : 0;
        uint Slot(uint flags)
        {
            uint texture = SpriteItem.TextureOf(flags);
            return texture == IMAGE_SLOT ? SpriteItem.WithTexture(flags, slot0) : texture == IMAGE_SLOT + 1 ? SpriteItem.WithTexture(flags, slot1) : flags;
        }

        int items0 = itemCount, frames0 = frameList.Frames.Count, clips0 = frameList.Clips.Count;
        int count = was.ItemsEnd - was.Items;
        while (itemCount + count > items.Length)
            Array.Resize(ref items, items.Length * 2);
        before.items.AsSpan(was.Items, count).CopyTo(items.AsSpan(itemCount));
        for (int i = itemCount; i < itemCount + count; i++)
            items[i].Flags = Slot(items[i].Flags);
        itemCount += count;

        // Frames and clips inside of it numbered again among this list's, and the ones it was inside of are the ones
        // it's inside of now
        int Frame(int index) => index >= was.Frames && index < was.FramesEnd ? index - was.Frames + frames0 : frameAt;
        int Clip(int index) => index >= was.Clips && index < was.ClipsEnd ? index - was.Clips + clips0 : clipAt;

        for (int i = was.Frames; i < was.FramesEnd; i++)
        {
            UIFrame frame = before.frameList.Frames[i];
            frameList.Frames.Add(frame with { Parent = Frame(frame.Parent) });
        }

        for (int i = was.Clips; i < was.ClipsEnd; i++)
        {
            UIFrameClip cut = before.frameList.Clips[i];
            frameList.Clips.Add(cut with { Frame = Frame(cut.Frame), Parent = Clip(cut.Parent),
                StationaryParentFrame = cut.StationaryParentFrame is { } outside ? Frame(outside) : null });
        }

        for (int i = was.Quads; i < was.QuadsEnd; i++)
        {
            ref UIFrameQuad quad = ref frameList.Add();
            quad = before.frameList.Quads[i];
            quad.Frame = Frame(quad.Frame);
            quad.Clip = Clip(quad.Clip);
            quad.Flags = Slot(quad.Flags);
        }

        for (int i = was.Homes; i < was.HomesEnd; i++)
        {
            var (id, frame) = before.homeOrder[i];
            int home = Frame(frame);
            homeOrder.Add((id, home));
            homes.TryAdd(id, home);
        }

        records[paintId] = was with
        {
            Items = items0, ItemsEnd = itemCount, Quads = frameList.QuadCount - (was.QuadsEnd - was.Quads), QuadsEnd = frameList.QuadCount,
            Frames = frames0, FramesEnd = frameList.Frames.Count, Clips = clips0, ClipsEnd = frameList.Clips.Count,
            Homes = homeOrder.Count - (was.HomesEnd - was.Homes), HomesEnd = homeOrder.Count, Image0 = runImage0, Image1 = runImage1
        };
    }

    // Helper method for where what a component painted now differs from what it painted at the same version before, for CheckReuse
    private string? Differs(UIDrawList before, in PaintRecord was, int items0, int quads0)
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

        return null;
    }
}
