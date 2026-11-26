using System.Numerics;

using Horizon.Graphics;
using Horizon.Rendering.Spriting;

namespace Horizon.UI.Drawing;

/// <summary>
/// A stretch of a list that moves as one, what's inside a scroll, an animated component, a module. How far its content
/// was moved inside the layout (<see cref="Shift"/>, a scroll's offset), and how that layout comes out on screen (the
/// visual transform of the animations it's in and its module's). Its key says which frame of the tick before it is.
/// </summary>
internal readonly record struct UIFrame(Vector2 Key, int Parent, Vector2 Shift, Vector2 VisualScale, Vector2 VisualOffset, Vector2 Scale, Vector2 Origin)
{
    /// <summary>
    /// Whether it goes where the pointer goes (a window being dragged), moved on every frame drawn by however far the
    /// pointer is from where it was when this was painted, <see cref="FollowFrom"/>, on the screen.
    /// </summary>
    public bool Follows { get; init; }

    public Vector2 FollowFrom { get; init; }

    /// <summary>
    /// Whether its <see cref="Shift"/> jumped this tick rather than moved (a zoom kept its place, the scroll went somewhere
    /// else with it), so a frame between this tick and the last doesn't slide what's in it over from the old shift.
    /// </summary>
    public bool Jumped { get; init; }

    /// <summary>
    /// Where the layout put the component it's the frame of, less what it's scrolled by (see <see cref="Shift"/>), while
    /// the layout is moving things about (something sliding or folding, see <see cref="UIDrawList.LayoutMoves"/>). A
    /// frame inside it has the same. Moved between two ticks, what's in the frame moves with it.
    /// </summary>
    public Vector2 Laid { get; init; }

    /// <summary>
    /// How far it's faded in, the animations it's in all together. Its quads keep their colours as they were asked for
    /// and are faded by this when they're resolved, so a fade between two ticks fades smoothly too.
    /// </summary>
    public float Opacity { get; init; } = 1.0f;

    /// <summary>Where a point of the layout is on screen, worked out the way a quad always was so the two agree to the bit.</summary>
    public Vector2 ToScreen(Vector2 layout) => (layout * VisualScale + VisualOffset) * Scale + Origin;
}

/// <summary>
/// A clip as it was pushed, where the layout had it, cut down further by the one it was pushed inside of. It moves with
/// the frame it was pushed in.
/// </summary>
internal readonly record struct UIFrameClip(UIRect Rect, int Frame, int Parent)
{
    // A viewport established before a visual translation, such as the stationary edge of overscrolled content.
    public int? StationaryParentFrame { get; init; }
}

/// <summary>
/// Hot per-quad metadata (20 bytes) touched by stationary fast-copy in <see cref="UIFrameList.Resolve"/> and
/// index rebasing in <see cref="UIDrawList"/> reuse. Cold layout geometry lives in the parallel
/// <see cref="UIQuadGeometry"/> array, and rare slant/rotation parameters live in <see cref="UIQuadExtra"/>.
/// </summary>
internal struct UIFrameQuad
{
    public int Frame, Clip;

    // Index in UIDrawList.items when emitted during painting, or -1 if culled
    public int Item;

    public uint Flags;

    // Index in UIFrameList.Extras for slanted/turned quads, or -1 for the >99% of quads that need neither
    public int Extra;
}

/// <summary>
/// Cold layout-space geometry for a frame quad, bulk-copied on <see cref="UIFrameList.CopyFrom"/> and
/// <see cref="UIDrawList"/> reuse and only read by <see cref="UIFrameList.Resolve"/> when a quad's frame or clip moved.
/// </summary>
internal struct UIQuadGeometry
{
    public UIRect Rect;
    public Vector2 TexMin, TexMax;
    public Vector2 Key;
    public uint Color;
    public float Ring;

    // How far it's faded by the edges it's near (see UIDrawList.PushFade), as it was painted, 1 for not at all
    public float Fade;
}

/// <summary>
/// Sparse side-table entry for the &lt;1% of frame quads that lean (italic SDF glyphs) or turn (spinning icons).
/// </summary>
internal struct UIQuadExtra
{
    public float Slant;
    public float Upright;
    public float Turn;
    public Vector2 Pivot;
}

/// <summary>A pane of glass as it was put down, with the quad index before its own shadow (<see cref="Under"/>) and the quad index it goes under (<see cref="Start"/>), both in frame-quad indices.</summary>
internal readonly record struct UIFrameGlass(int Under, int Start, UIRect Rect, int Frame, int Clip, float Radius, float Blur, float Bend, float Lens, uint Tint, int ItemUnder = -1, int ItemStart = -1);

/// <summary>Where a run of quads that show the same images ended, by its place among the frame quads and emitted items.</summary>
internal readonly record struct UIFrameRun(int End, Texture? Image0, Texture? Image1, int ItemEnd = -1, int Used0Quad = -1, int Used1Quad = -1);

/// <summary>
/// What a list painted as frames, alongside the quads it painted as they are on screen. Kept from one tick to be put
/// together with the next one's into a frame between the two (see <see cref="UIInBetween"/>), and resolved back into
/// quads, with the frames as they were painted or moved to somewhere between two ticks.
/// </summary>
internal sealed class UIFrameList
{
    private static readonly UIRect Unclipped = new(new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity));

    public readonly List<UIFrame> Frames = [];
    public readonly List<UIFrameClip> Clips = [];
    public readonly List<UIFrameGlass> Glass = [];
    public readonly List<UIFrameRun> Runs = [];
    public readonly List<UIQuadExtra> Extras = [];
    public UIFrameQuad[] Quads = new UIFrameQuad[512];
    public UIQuadGeometry[] QuadGeoms = new UIQuadGeometry[512];
    public int QuadCount;

    /// <summary>Whether any of its frames goes where the pointer goes, see <see cref="UIFrame.Follows"/>.</summary>
    public bool Follows;

    /// <summary>Monotonically incremented every time this list is cleared for a new paint, for caches keyed on its frames.</summary>
    public long Stamp { get; private set; }

    public ReadOnlySpan<UIFrame> Span => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Frames);

    public void Clear()
    {
        Stamp++;
        Frames.Clear();
        Clips.Clear();
        Glass.Clear();
        Runs.Clear();
        Extras.Clear();
        QuadCount = 0;
        Follows = false;
    }

    /// <summary>Takes a copy of another list's frames, for a tick captured.</summary>
    public void CopyFrom(UIFrameList other)
    {
        Clear();
        Frames.AddRange(other.Frames);
        Clips.AddRange(other.Clips);
        Glass.AddRange(other.Glass);
        Runs.AddRange(other.Runs);
        if (other.Extras.Count > 0)
            Extras.AddRange(other.Extras);
        if (Quads.Length < other.QuadCount)
        {
            int cap = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)other.QuadCount);
            Quads = new UIFrameQuad[cap];
            QuadGeoms = new UIQuadGeometry[cap];
        }
        other.Quads.AsSpan(0, other.QuadCount).CopyTo(Quads);
        other.QuadGeoms.AsSpan(0, other.QuadCount).CopyTo(QuadGeoms);
        QuadCount = other.QuadCount;
        Follows = other.Follows;
    }

    public int AddSlot()
    {
        if (QuadCount == Quads.Length)
        {
            int next = Quads.Length * 2;
            Array.Resize(ref Quads, next);
            Array.Resize(ref QuadGeoms, next);
        }
        return QuadCount++;
    }

    /// <summary>Grows the frame-quad buffers once so a reused component can bulk-copy its quads without per-quad resize checks.</summary>
    public void EnsureCapacity(int required)
    {
        if (Quads.Length >= required)
            return;
        int next = Quads.Length == 0 ? 512 : Quads.Length;
        while (next < required)
            next *= 2;
        Array.Resize(ref Quads, next);
        Array.Resize(ref QuadGeoms, next);
    }

    // Clips worked out by Resolve, every one once
    private UIRect[] resolvedClips = [];

    // How far every frame's content has moved from where it was painted, worked out by Resolve once a frame rather than
    // once a quad, see Moved
    private Vector2[] resolvedMoves = [];

    // Resolved quad counts at each glass pane's Under mark, reused across Resolve calls
    private int[] resolvedUnder = [];

    // Per-frame and per-clip unchanged flags for stationary-frame fast copy, reused across Resolve calls
    private bool[] frameUnchanged = [];
    private bool[] clipUnchanged = [];

    /// <summary>
    /// The quads, runs and glass the frames come to on screen, as the list painted them. With the frames as they were
    /// painted that's what the list has already (see <see cref="UIDrawList.FramesDiffer"/>), with frames moved between two ticks
    /// it's the frame in between.
    /// </summary>
    /// <param name="paintedItems">
    /// The painted <see cref="SpriteItem"/> buffer corresponding to this frame list. When non-empty, quads whose frame and
    /// clip are unchanged from where they were painted are copied directly without recomputing transforms, clipping, or UVs.
    /// </param>
    /// <returns>How many quads went into <paramref name="into"/>, which is made bigger when they don't fit.</returns>
    public int Resolve(ReadOnlySpan<UIFrame> at, ref SpriteItem[] into, List<UIDrawList.Run> runsInto, List<UIGlassPane> glassInto, ReadOnlySpan<SpriteItem> paintedItems = default)
    {
        runsInto.Clear();
        glassInto.Clear();

        bool useFastCopy = !paintedItems.IsEmpty;

        if (resolvedMoves.Length < at.Length)
            resolvedMoves = new Vector2[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)at.Length)];
        if (useFastCopy && frameUnchanged.Length < at.Length)
            frameUnchanged = new bool[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)at.Length)];
        ReadOnlySpan<UIFrame> painted = Span;
        int frameCount = Math.Min(at.Length, painted.Length);
        for (int i = 0; i < frameCount; i++)
        {
            ref readonly UIFrame current = ref at[i];
            ref readonly UIFrame orig = ref painted[i];
            resolvedMoves[i] = Moved(current, orig);
            if (useFastCopy)
                frameUnchanged[i] = FrameUnchanged(current, orig);
        }
        for (int i = frameCount; i < at.Length; i++)
        {
            resolvedMoves[i] = Vector2.Zero;
            if (useFastCopy)
                frameUnchanged[i] = false;
        }

        if (resolvedClips.Length < Clips.Count)
            resolvedClips = new UIRect[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Clips.Count)];
        if (useFastCopy && clipUnchanged.Length < Clips.Count)
            clipUnchanged = new bool[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Clips.Count)];
        ReadOnlySpan<UIFrameClip> clips = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Clips);
        for (int i = 0; i < clips.Length; i++)
        {
            ref readonly UIFrameClip cut = ref clips[i];
            if (useFastCopy)
            {
                bool unchanged = frameUnchanged[cut.Frame] && (cut.Parent < 0 || clipUnchanged[cut.Parent]);
                if (unchanged && cut.StationaryParentFrame is { } stationary)
                    unchanged = frameUnchanged[stationary];
                clipUnchanged[i] = unchanged;
            }
            UIRect rect = Shifted(cut.Rect, resolvedMoves[cut.Frame]);
            UIRect parent = cut.Parent < 0 ? Unclipped : resolvedClips[cut.Parent];
            if (cut.StationaryParentFrame is { } outside)
            {
                ref readonly UIFrame frame = ref at[cut.Frame];
                Vector2 translation = frame.VisualScale.X != 0 && frame.VisualScale.Y != 0
                    ? (frame.VisualOffset - at[outside].VisualOffset) / frame.VisualScale : Vector2.Zero;
                rect = Shifted(rect, -translation);
                parent = Shifted(parent, -translation);
            }
            resolvedClips[i] = parent.Intersect(rect);
        }

        ReadOnlySpan<UIFrameGlass> glassSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Glass);
        ReadOnlySpan<UIFrameRun> runSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Runs);
        ReadOnlySpan<UIQuadExtra> extraSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Extras);
        if (glassSpan.Length > 0 && resolvedUnder.Length < glassSpan.Length)
            resolvedUnder = new int[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)glassSpan.Length)];

        int count = 0, underNext = 0, glassNext = 0, runNext = 0, runFirst = 0;
        int nextUnder = glassSpan.Length > 0 ? glassSpan[0].Under : -1;
        int nextGlass = glassSpan.Length > 0 ? glassSpan[0].Start : -1;
        int nextRun = runSpan.Length > 0 ? runSpan[0].End : -1;
        for (int i = 0; i <= QuadCount; i++)
        {
            // The panes of glass that capture or go under this quad, and the runs that ended before it
            if (i == nextUnder)
            {
                do
                {
                    resolvedUnder[underNext++] = count;
                }
                while (underNext < glassSpan.Length && glassSpan[underNext].Under == i);
                nextUnder = underNext < glassSpan.Length ? glassSpan[underNext].Under : -1;
            }
            if (i == nextGlass)
            {
                do
                {
                    ResolveGlass(glassSpan[glassNext], at, resolvedUnder[glassNext], count, glassInto);
                    glassNext++;
                }
                while (glassNext < glassSpan.Length && glassSpan[glassNext].Start == i);
                nextGlass = glassNext < glassSpan.Length ? glassSpan[glassNext].Start : -1;
            }
            if (i == nextRun)
            {
                do
                {
                    if (count > runFirst)
                        runsInto.Add(new UIDrawList.Run(runFirst, count - runFirst, runSpan[runNext].Image0, runSpan[runNext].Image1));
                    runFirst = count;
                    runNext++;
                }
                while (runNext < runSpan.Length && runSpan[runNext].End == i);
                nextRun = runNext < runSpan.Length ? runSpan[runNext].End : -1;
            }

            if (i == QuadCount)
                break;

            ref readonly UIFrameQuad quad = ref Quads[i];
            if (useFastCopy && frameUnchanged[quad.Frame] && (quad.Clip < 0 || clipUnchanged[quad.Clip]))
            {
                int itemIndex = quad.Item;
                if (itemIndex < 0)
                    continue;
                if ((uint)itemIndex < (uint)paintedItems.Length)
                {
                    if (count == into.Length)
                        Array.Resize(ref into, Math.Max(512, into.Length * 2));
                    into[count++] = paintedItems[itemIndex];
                    continue;
                }
            }

            ref readonly UIQuadGeometry geom = ref QuadGeoms[i];
            ref readonly UIFrame frame = ref at[quad.Frame];
            Vector2 moved = resolvedMoves[quad.Frame];
            UIRect rect = Shifted(geom.Rect, moved);
            UIRect cut = quad.Clip < 0 ? Unclipped : resolvedClips[quad.Clip];
            UIRect visible = rect.Intersect(cut);
            if (visible.IsEmpty)
                continue;

            if (count == into.Length)
                Array.Resize(ref into, Math.Max(512, into.Length * 2));

            // A shape goes whole with its clip, the shader cuts it, see UIDrawList.Shape
            if ((quad.Flags & SpriteItem.ShapeFlag) != 0)
            {
                uint faded = geom.Color;
                float fadedBy = frame.Opacity * geom.Fade;
                if (fadedBy < 1.0f)
                {
                    uint alpha = (uint)((faded >> 24) * fadedBy + 0.5f);
                    faded = (faded & 0x00FFFFFF) | alpha << 24;
                }

                into[count++] = UIDrawList.ScreenShape(rect, cut, geom.TexMin, geom.Ring, faded, frame.VisualScale, frame.VisualOffset, frame.Scale, frame.Origin);
                continue;
            }

            // Trimmed and placed the way Quad does it, see there
            Vector2 texTopLeft = geom.TexMin, texBottomRight = geom.TexMax;
            if (visible != rect)
            {
                Vector2 texelsPerUnit = (texBottomRight - texTopLeft) / rect.Size;
                Vector2 trimmedTopLeft = new(visible.Min.X - rect.Min.X, rect.Max.Y - visible.Max.Y);
                Vector2 trimmedBottomRight = new(rect.Max.X - visible.Max.X, visible.Min.Y - rect.Min.Y);
                texTopLeft += trimmedTopLeft * texelsPerUnit;
                texBottomRight -= trimmedBottomRight * texelsPerUnit;
            }

            Vector2 min = frame.ToScreen(visible.Min), max = frame.ToScreen(visible.Max);
            ref SpriteItem item = ref into[count++];
            // Faded the way Quad fades it, by its frame and the edges it was near
            uint color = geom.Color;
            float shown = frame.Opacity * geom.Fade;
            if (shown < 1.0f)
            {
                uint alpha = (uint)((color >> 24) * shown + 0.5f);
                color = (color & 0x00FFFFFF) | alpha << 24;
            }

            item = SpriteItem.Rectangle(min, max, texTopLeft, texBottomRight, color, quad.Flags);
            item.Ring = geom.Ring;

            if (quad.Extra >= 0)
            {
                ref readonly UIQuadExtra extra = ref extraSpan[quad.Extra];
                if (extra.Slant != 0.0f)
                {
                    float pivot = frame.ToScreen(new Vector2(0.0f, extra.Upright + moved.Y)).Y;
                    item.Origin.X += extra.Slant * (min.Y - pivot);
                    item.AxisY.X = extra.Slant * (max.Y - min.Y);
                }

                if (extra.Turn != 0.0f && visible == rect)
                {
                    Vector2 pivot = frame.ToScreen(extra.Pivot + moved);
                    var (sin, cos) = MathF.SinCos(extra.Turn);
                    item.Origin = pivot + TurnVec(item.Origin - pivot, sin, cos);
                    item.AxisX = TurnVec(item.AxisX, sin, cos);
                    item.AxisY = TurnVec(item.AxisY, sin, cos);
                }
            }

            item.Key = geom.Key;
        }

        return count;
    }

    private static Vector2 TurnVec(Vector2 v, float sin, float cos) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);

    /// <summary>
    /// Resolves only the glass panes of a stationary frame without iterating quads, using the item indices recorded
    /// at paint time. Returns false if any glass pane lacks recorded item indices.
    /// </summary>
    public bool ResolveStationaryGlass(List<UIGlassPane> glassInto)
    {
        glassInto.Clear();
        ReadOnlySpan<UIFrameGlass> glassSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Glass);
        if (glassSpan.IsEmpty)
            return true;

        for (int i = 0; i < glassSpan.Length; i++)
        {
            if (glassSpan[i].ItemUnder < 0 || glassSpan[i].ItemStart < 0)
                return false;
        }

        ReadOnlySpan<UIFrame> at = Span;
        if (resolvedClips.Length < Clips.Count)
            resolvedClips = new UIRect[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Clips.Count)];
        ReadOnlySpan<UIFrameClip> clips = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Clips);
        for (int i = 0; i < clips.Length; i++)
        {
            ref readonly UIFrameClip cut = ref clips[i];
            UIRect rect = cut.Rect;
            UIRect parent = cut.Parent < 0 ? Unclipped : resolvedClips[cut.Parent];
            if (cut.StationaryParentFrame is { } outside)
            {
                ref readonly UIFrame frame = ref at[cut.Frame];
                Vector2 translation = frame.VisualScale.X != 0 && frame.VisualScale.Y != 0
                    ? (frame.VisualOffset - at[outside].VisualOffset) / frame.VisualScale : Vector2.Zero;
                rect = Shifted(rect, -translation);
                parent = Shifted(parent, -translation);
            }
            resolvedClips[i] = parent.Intersect(rect);
        }

        for (int i = 0; i < glassSpan.Length; i++)
        {
            ref readonly UIFrameGlass pane = ref glassSpan[i];
            ref readonly UIFrame frame = ref at[pane.Frame];
            UIRect cut = pane.Clip < 0 ? Unclipped : resolvedClips[pane.Clip];
            if (pane.Rect.Intersect(cut).IsEmpty)
                continue;

            Vector2 clipMin = float.IsFinite(cut.Min.X) && float.IsFinite(cut.Min.Y) ? frame.ToScreen(cut.Min) : new Vector2(float.NegativeInfinity);
            Vector2 clipMax = float.IsFinite(cut.Max.X) && float.IsFinite(cut.Max.Y) ? frame.ToScreen(cut.Max) : new Vector2(float.PositiveInfinity);
            glassInto.Add(new UIGlassPane(pane.ItemStart, frame.ToScreen(pane.Rect.Min), frame.ToScreen(pane.Rect.Max), clipMin, clipMax, pane.Radius, pane.Blur, MathF.Min(1.0f, frame.Opacity * UIDrawList.GLASS_LEAD), pane.Bend, pane.ItemUnder, pane.Lens, pane.Tint));
        }

        return true;
    }

    // Helper method for a pane of glass on screen, the way Glass puts it down
    private void ResolveGlass(in UIFrameGlass pane, ReadOnlySpan<UIFrame> at, int under, int start, List<UIGlassPane> into)
    {
        ref readonly UIFrame frame = ref at[pane.Frame];
        UIRect rect = Shifted(pane.Rect, resolvedMoves[pane.Frame]);
        UIRect cut = pane.Clip < 0 ? Unclipped : resolvedClips[pane.Clip];
        if (rect.Intersect(cut).IsEmpty)
            return;

        Vector2 clipMin = float.IsFinite(cut.Min.X) && float.IsFinite(cut.Min.Y) ? frame.ToScreen(cut.Min) : new Vector2(float.NegativeInfinity);
        Vector2 clipMax = float.IsFinite(cut.Max.X) && float.IsFinite(cut.Max.Y) ? frame.ToScreen(cut.Max) : new Vector2(float.PositiveInfinity);
        into.Add(new UIGlassPane(start, frame.ToScreen(rect.Min), frame.ToScreen(rect.Max), clipMin, clipMax, pane.Radius, pane.Blur, MathF.Min(1.0f, frame.Opacity * UIDrawList.GLASS_LEAD), pane.Bend, under, pane.Lens, pane.Tint));
    }

    // Helper method for how far a frame's content has moved from where it was painted, nothing for the frames as painted
    private static Vector2 Moved(in UIFrame at, in UIFrame painted) => at.Shift - painted.Shift + (at.Laid - painted.Laid);

    // Helper method for whether a frame's transform and opacity match how it was painted
    private static bool FrameUnchanged(in UIFrame at, in UIFrame painted) =>
        at.Shift == painted.Shift &&
        at.Laid == painted.Laid &&
        at.VisualScale == painted.VisualScale &&
        at.VisualOffset == painted.VisualOffset &&
        at.Scale == painted.Scale &&
        at.Origin == painted.Origin &&
        at.Opacity == painted.Opacity;

    private static UIRect Shifted(UIRect rect, Vector2 by) =>
        by == Vector2.Zero || !float.IsFinite(rect.Min.X) ? rect : new UIRect(rect.Min + by, rect.Max + by);

}

// What the list painted as frames, alongside the quads it painted as they are on screen, see UIFrameList. The quads are
// what's drawn when nothing moves between two ticks, the frames are what a frame between two ticks is put together
// from, and FramesDiffer holds the one up against the other
public sealed partial class UIDrawList
{
    private readonly UIFrameList frameList = new();

    // The frame and the clip painting goes into now, and the ones to go back to
    private readonly Stack<(int Frame, int Clip, Vector2 Shift)> openFrames = new();
    private readonly Stack<int> openClips = new();
    private int frameAt;
    private int clipAt = -1;
    private Vector2 shift, laid;

    /// <summary>
    /// Whether the layout is moving things about this tick or did the last (a drawer sliding, a panel folding, and so
    /// everything round it laid out somewhere else every tick), so every component is painted in a frame of its own
    /// that knows where it was laid out (see <see cref="UIFrame.Laid"/>), and a frame between two ticks moves them all
    /// smoothly. Otherwise something the layout put somewhere else is put there, not slid over from where it was.
    /// </summary>
    internal bool LayoutMoves { get; set; }

    /// <summary>
    /// Whether every frame opened from here on jumped this tick rather than moved (see <see cref="UIFrame.Jumped"/>): all of
    /// a component laid out anew at another size, a zoom, whose lenses and hints slid over from where they were at their
    /// own speeds while the code jumped.
    /// </summary>
    internal bool JumpsFrames { get; set; }

    // How many frames every owner has opened so far this paint, which is what a frame is named by after its owner
    private readonly Dictionary<float, int> framesOpened = [];

    // The frame every component was painted in, by its name, for finding where it's drawn, see HomeOf
    private readonly Dictionary<float, int> homes = [];

    /// <summary>The frame a component was painted in (the frame it was in when it started painting), false if it wasn't.</summary>
    internal bool HomeOf(float paintId, out int frame) => homes.TryGetValue(paintId, out frame);

    // Helper method to say a component starts painting in the frame painting goes into now
    private void NoteHome(float paintId)
    {
        if (homes.TryAdd(paintId, frameAt))
            homeOrder.Add((paintId, frameAt));
    }

    internal UIFrameList FrameList => frameList;

    // Helper method to start the frames over with the list, the first one the whole of it unmoved
    private void BeginFrames()
    {
        frameList.Clear();
        openFrames.Clear();
        openClips.Clear();
        framesOpened.Clear();
        homes.Clear();
        BeginRecords();
        shift = laid = Vector2.Zero;
        LayoutMoves = false;
        JumpsFrames = false;
        clipAt = -1;
        frameAt = -1;
        OpenFrame();
    }

    /// <summary>
    /// What's painted from here on until <see cref="PopFrame"/> is the content of something that moves it about inside
    /// itself, by <paramref name="moved"/> in the layout's units, a scroll by its offset. The content is laid out where
    /// it's moved to as always, and its frame knows how much of that is the moving, so a frame between two ticks can
    /// move it smoothly while what's in it stays the newer tick's.
    /// </summary>
    /// <remarks>
    /// It's moved by whole pixels of the screen. Whatever's a fraction of a pixel over is taken back off it, so what's
    /// in it keeps the same place between the pixels tick after tick while it scrolls, and nothing in it shimmers or
    /// wobbles against the rest. A scroll eases by fractions of a pixel, and moved by those the words snapped to the
    /// pixels a tick at a time while the boxes under them glided.
    /// </remarks>
    /// <param name="jumped">Whether the move jumped this tick rather than slid, see <see cref="UIFrame.Jumped"/>.</param>
    public void PushFrame(Vector2 moved, bool jumped = false)
    {
        openFrames.Push((frameAt, clipAt, shift));
        visuals.Push((visualOffset, visualScale, opacity));
        Vector2 across = visualScale * scale;
        if (across.X != 0.0f && across.Y != 0.0f)
        {
            Vector2 onScreen = moved * across;
            visualOffset += (Vector2.Round(onScreen) - onScreen) / scale;
        }

        shift += moved;
        OpenFrame();
        if (jumped)
            frameList.Frames[frameAt] = frameList.Frames[frameAt] with { Jumped = true };
    }

    /// <summary>Goes back to the frame from before <see cref="PushFrame"/>.</summary>
    public void PopFrame()
    {
        (frameAt, _, shift) = openFrames.Pop();
        (visualOffset, visualScale, opacity) = visuals.Pop();
    }

    // Helper method to open a frame where painting is now, inside of the one it's in
    private void OpenFrame()
    {
        // By who opened it and how many they had opened before it, never by how many quads they'd painted, a highlight
        // painted before a list's rows would have had its rows' frame taken for another
        int opened = System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(framesOpened, owner, out _)++;
        frameList.Frames.Add(new UIFrame(new Vector2(owner, -1 - opened), frameAt, shift, visualScale, visualOffset, scale, origin) { Laid = laid, Opacity = opacity, Jumped = JumpsFrames });
        frameAt = frameList.Frames.Count - 1;
    }

    // Helper method for a visual pushed, its own frame inside of the one it's in
    private void PushVisualFrame()
    {
        openFrames.Push((frameAt, clipAt, shift));
        OpenFrame();
    }

    private void PopVisualFrame() => (frameAt, _, _) = openFrames.Pop();

    /// <summary>
    /// What's painted from here on until <see cref="PopFollowFrame"/> goes where the pointer goes once it's on screen,
    /// see <see cref="UIFrame.Follows"/>. A window being dragged, which painted at the pointer of its tick was always
    /// a tick or more behind it, and a lot more behind when ticks came slowly.
    /// </summary>
    /// <param name="pointer">Where the pointer was when this was laid out, in the same units the quads come out in.</param>
    internal void PushFollowFrame(Vector2 pointer)
    {
        Uncacheable();
        openFrames.Push((frameAt, clipAt, shift));
        OpenFrame();
        frameList.Frames[frameAt] = frameList.Frames[frameAt] with { Follows = true, FollowFrom = pointer };
        frameList.Follows = true;
    }

    internal void PopFollowFrame() => (frameAt, _, _) = openFrames.Pop();

    // What a component painted in a frame of where it was laid out goes back to, see PushLaidFrame
    private readonly Stack<Vector2> openLaid = new();

    /// <summary>What's painted from here on until <see cref="PopLaidFrame"/> is a component laid out at a place, see <see cref="LayoutMoves"/>.</summary>
    internal void PushLaidFrame(Vector2 place)
    {
        Uncacheable();
        openFrames.Push((frameAt, clipAt, shift));
        openLaid.Push(laid);
        laid = place - shift;
        OpenFrame();
    }

    internal void PopLaidFrame()
    {
        (frameAt, _, _) = openFrames.Pop();
        laid = openLaid.Pop();
    }

    // Helper method for a module moved and scaled, a frame of its own at the top
    private void TransformFrame()
    {
        frameAt = -1;
        OpenFrame();
    }

    private void PushFrameClip(UIRect rect, int? stationaryParentFrame = null)
    {
        openClips.Push(clipAt);
        frameList.Clips.Add(new UIFrameClip(rect, frameAt, clipAt) { StationaryParentFrame = stationaryParentFrame });
        clipAt = frameList.Clips.Count - 1;
    }

    private void PopFrameClip() => clipAt = openClips.Pop();

    // Helper method to keep a quad as it was asked for, see Quad
    private void KeepFrameQuad(UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, uint color, uint flags, float ring, float slant, float upright, float fade = 1.0f, int item = -1)
    {
        int extraIndex = -1;
        if (slant != 0.0f)
        {
            extraIndex = frameList.Extras.Count;
            frameList.Extras.Add(new UIQuadExtra { Slant = slant, Upright = upright });
        }

        int slot = frameList.AddSlot();
        frameList.Quads[slot] = new UIFrameQuad
        {
            Frame = frameAt,
            Clip = clipAt,
            Item = item,
            Flags = flags,
            Extra = extraIndex
        };
        frameList.QuadGeoms[slot] = new UIQuadGeometry
        {
            Rect = rect,
            TexMin = texTopLeft,
            TexMax = texBottomRight,
            Key = new Vector2(owner, ownerPainted),
            Color = color,
            Ring = ring,
            Fade = fade
        };
    }

    // Helper method for the last quad kept turned about a point of the layout, see Icon
    private void TurnFrameQuad(float turn, Vector2 pivot)
    {
        ref UIFrameQuad quad = ref frameList.Quads[frameList.QuadCount - 1];
        if (quad.Extra >= 0)
        {
            ref UIQuadExtra extra = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(frameList.Extras)[quad.Extra];
            extra.Turn = turn;
            extra.Pivot = pivot;
        }
        else
        {
            quad.Extra = frameList.Extras.Count;
            frameList.Extras.Add(new UIQuadExtra { Turn = turn, Pivot = pivot });
        }
    }

    private void KeepFrameGlass(int under, UIRect rect, float radius, float blur, float bend, float lens, uint tint, int itemUnder = -1, int itemStart = -1) =>
        frameList.Glass.Add(new UIFrameGlass(under, frameList.QuadCount, rect, frameAt, clipAt, radius, blur, bend, lens, tint, itemUnder, itemStart));

    private void KeepFrameRun(Texture? image0, Texture? image1, int itemEnd = -1, int used0Quad = -1, int used1Quad = -1) =>
        frameList.Runs.Add(new UIFrameRun(frameList.QuadCount, image0, image1, itemEnd, used0Quad, used1Quad));

    /// <summary>
    /// Where the quads the frames resolve to differ from the ones the list painted, null where they don't, for the check
    /// that the frames are the whole of what was painted. Corners within a hundredth of a unit of each other are the
    /// same, a rectangle taken off its frame's move and put back can be a bit out.
    /// </summary>
    internal string? FramesDiffer()
    {
        SpriteItem[] resolved = [];
        var runsBack = new List<Run>();
        var glassBack = new List<UIGlassPane>();
        int count = frameList.Resolve(frameList.Span, ref resolved, runsBack, glassBack);

        if (count != itemCount)
            return $"{count} quads resolved, {itemCount} painted";
        if (!runsBack.SequenceEqual(runs))
            return $"the runs differ, {runsBack.Count} resolved and {runs.Count} painted";
        if (glassBack.Count != glass.Count)
            return $"{glassBack.Count} panes of glass resolved, {glass.Count} painted";

        for (int i = 0; i < count; i++)
        {
            SpriteItem a = resolved[i], b = items[i];
            if (a.Key != b.Key || a.Flags != b.Flags || a.Color != b.Color || a.Ring != b.Ring ||
                !Near(a.Origin, b.Origin) || !Near(a.AxisX, b.AxisX) || !Near(a.AxisY, b.AxisY) || !Near(a.TexMin, b.TexMin) || !Near(a.TexMax, b.TexMax))
                return $"quad {i} of {count} differs, resolved {a.Origin} {a.AxisX} {a.AxisY} {a.TexMin} {a.TexMax} key {a.Key}, painted {b.Origin} {b.AxisX} {b.AxisY} {b.TexMin} {b.TexMax} key {b.Key}";
        }

        for (int i = 0; i < glass.Count; i++)
        {
            UIGlassPane a = glassBack[i], b = glass[i];
            if (a.Under != b.Under || a.Start != b.Start || !Near(a.Min, b.Min) || !Near(a.Max, b.Max) || !Near(a.ClipMin, b.ClipMin) || !Near(a.ClipMax, b.ClipMax) ||
                a.Radius != b.Radius || a.Blur != b.Blur || a.Opacity != b.Opacity || a.Bend != b.Bend || a.Lens != b.Lens || a.Tint != b.Tint)
                return $"pane of glass {i} differs, resolved {a}, painted {b}";
        }

        return null;

        static bool Near(Vector2 a, Vector2 b) => a == b || Vector2.Distance(a, b) < 0.01f;
    }
}
