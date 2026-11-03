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
    /// <summary>Where a point of the layout is on screen, worked out the way a quad always was so the two agree to the bit.</summary>
    public Vector2 ToScreen(Vector2 layout) => (layout * VisualScale + VisualOffset) * Scale + Origin;
}

/// <summary>
/// A clip as it was pushed, where the layout had it, cut down further by the one it was pushed inside of. It moves with
/// the frame it was pushed in.
/// </summary>
internal readonly record struct UIFrameClip(UIRect Rect, int Frame, int Parent);

/// <summary>
/// A quad as it was asked for, before clipping, where the layout had it when it was painted. It moves with its frame,
/// and what is worked out of where that is (its corners on screen, the part of its texture left once it's clipped) is
/// worked out again whenever it's resolved. Kept where it was painted and not taken off its frame's shift, or put
/// back it came out a bit out, and one that only just touched a clip's edge was cut away.
/// </summary>
internal struct UIFrameQuad
{
    public UIRect Rect;
    public Vector2 TexMin, TexMax;
    public uint Color, Flags;
    public float Ring, Slant, Upright;
    public Vector2 Key;
    public int Frame, Clip;

    // Turned about a point (an icon that spins), only while it's all in sight
    public float Turn;
    public Vector2 Pivot;
}

/// <summary>A pane of glass as it was put down, before the quad it goes under (by its place among the frame quads).</summary>
internal readonly record struct UIFrameGlass(int Start, UIRect Rect, int Frame, int Clip, float Radius, float Blur, float Opacity, float Bend);

/// <summary>Where a run of quads that show the same images ended, by its place among the frame quads.</summary>
internal readonly record struct UIFrameRun(int End, Texture? Image0, Texture? Image1);

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
    public UIFrameQuad[] Quads = new UIFrameQuad[512];
    public int QuadCount;

    public ReadOnlySpan<UIFrame> Span => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Frames);

    public void Clear()
    {
        Frames.Clear();
        Clips.Clear();
        Glass.Clear();
        Runs.Clear();
        QuadCount = 0;
    }

    /// <summary>Takes a copy of another list's frames, for a tick captured.</summary>
    public void CopyFrom(UIFrameList other)
    {
        Clear();
        Frames.AddRange(other.Frames);
        Clips.AddRange(other.Clips);
        Glass.AddRange(other.Glass);
        Runs.AddRange(other.Runs);
        if (Quads.Length < other.QuadCount)
            Quads = new UIFrameQuad[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)other.QuadCount)];
        other.Quads.AsSpan(0, other.QuadCount).CopyTo(Quads);
        QuadCount = other.QuadCount;
    }

    public ref UIFrameQuad Add()
    {
        if (QuadCount == Quads.Length)
            Array.Resize(ref Quads, Quads.Length * 2);
        return ref Quads[QuadCount++];
    }

    // Clips worked out by Resolve, every one once
    private UIRect[] resolvedClips = [];

    /// <summary>
    /// The quads, runs and glass the frames come to on screen, as the list painted them. With the frames as they were
    /// painted that's what the list has already (see <see cref="UIDrawList.FramesDiffer"/>), with frames moved between two ticks
    /// it's the frame in between.
    /// </summary>
    /// <returns>How many quads went into <paramref name="into"/>, which is made bigger when they don't fit.</returns>
    public int Resolve(ReadOnlySpan<UIFrame> at, ref SpriteItem[] into, List<UIDrawList.Run> runsInto, List<UIGlassPane> glassInto)
    {
        runsInto.Clear();
        glassInto.Clear();

        if (resolvedClips.Length < Clips.Count)
            resolvedClips = new UIRect[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Clips.Count)];
        for (int i = 0; i < Clips.Count; i++)
        {
            UIFrameClip cut = Clips[i];
            UIRect rect = Shifted(cut.Rect, Moved(at, cut.Frame));
            resolvedClips[i] = cut.Parent < 0 ? Unclipped.Intersect(rect) : resolvedClips[cut.Parent].Intersect(rect);
        }

        int count = 0, glassNext = 0, runNext = 0, runFirst = 0;
        for (int i = 0; i <= QuadCount; i++)
        {
            // The panes of glass that go under this quad, and the runs that ended before it
            for (; glassNext < Glass.Count && Glass[glassNext].Start == i; glassNext++)
                ResolveGlass(Glass[glassNext], at, count, glassInto);
            for (; runNext < Runs.Count && Runs[runNext].End == i; runNext++)
            {
                if (count > runFirst)
                    runsInto.Add(new UIDrawList.Run(runFirst, count - runFirst, Runs[runNext].Image0, Runs[runNext].Image1));
                runFirst = count;
            }

            if (i == QuadCount)
                break;

            ref readonly UIFrameQuad quad = ref Quads[i];
            UIFrame frame = at[quad.Frame];
            Vector2 moved = Moved(at, quad.Frame);
            UIRect rect = Shifted(quad.Rect, moved);
            UIRect cut = quad.Clip < 0 ? Unclipped : resolvedClips[quad.Clip];
            UIRect visible = rect.Intersect(cut);
            if (visible.IsEmpty)
                continue;

            // Trimmed and placed the way Quad does it, see there
            Vector2 texTopLeft = quad.TexMin, texBottomRight = quad.TexMax;
            if (visible != rect)
            {
                Vector2 texelsPerUnit = (texBottomRight - texTopLeft) / rect.Size;
                Vector2 trimmedTopLeft = new(visible.Min.X - rect.Min.X, rect.Max.Y - visible.Max.Y);
                Vector2 trimmedBottomRight = new(rect.Max.X - visible.Max.X, visible.Min.Y - rect.Min.Y);
                texTopLeft += trimmedTopLeft * texelsPerUnit;
                texBottomRight -= trimmedBottomRight * texelsPerUnit;
            }

            if (count == into.Length)
                Array.Resize(ref into, Math.Max(512, into.Length * 2));

            Vector2 min = frame.ToScreen(visible.Min), max = frame.ToScreen(visible.Max);
            ref SpriteItem item = ref into[count++];
            item = SpriteItem.Rectangle(min, max, texTopLeft, texBottomRight, quad.Color, quad.Flags);
            item.Ring = quad.Ring;

            if (quad.Slant != 0.0f)
            {
                float pivot = frame.ToScreen(new Vector2(0.0f, quad.Upright + moved.Y)).Y;
                item.Origin.X += quad.Slant * (min.Y - pivot);
                item.AxisY.X = quad.Slant * (max.Y - min.Y);
            }

            if (quad.Turn != 0.0f && visible == rect)
            {
                Vector2 pivot = frame.ToScreen(quad.Pivot + moved);
                var (sin, cos) = MathF.SinCos(quad.Turn);
                Vector2 Turned(Vector2 v) => new(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
                item.Origin = pivot + Turned(item.Origin - pivot);
                item.AxisX = Turned(item.AxisX);
                item.AxisY = Turned(item.AxisY);
            }

            item.Key = quad.Key;
        }

        return count;
    }

    // Helper method for a pane of glass on screen, the way Glass puts it down
    private void ResolveGlass(in UIFrameGlass pane, ReadOnlySpan<UIFrame> at, int start, List<UIGlassPane> into)
    {
        UIFrame frame = at[pane.Frame];
        UIRect rect = Shifted(pane.Rect, Moved(at, pane.Frame));
        UIRect cut = pane.Clip < 0 ? Unclipped : resolvedClips[pane.Clip];
        if (rect.Intersect(cut).IsEmpty)
            return;

        Vector2 clipMin = float.IsFinite(cut.Min.X) && float.IsFinite(cut.Min.Y) ? frame.ToScreen(cut.Min) : new Vector2(float.NegativeInfinity);
        Vector2 clipMax = float.IsFinite(cut.Max.X) && float.IsFinite(cut.Max.Y) ? frame.ToScreen(cut.Max) : new Vector2(float.PositiveInfinity);
        into.Add(new UIGlassPane(start, frame.ToScreen(rect.Min), frame.ToScreen(rect.Max), clipMin, clipMax, pane.Radius, pane.Blur, pane.Opacity, pane.Bend));
    }

    // Helper method for how far a frame's content has moved from where it was painted, nothing for the frames as painted
    private Vector2 Moved(ReadOnlySpan<UIFrame> at, int frame) => at[frame].Shift - Frames[frame].Shift;

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
    private Vector2 shift;

    // How many frames every owner has opened so far this paint, which is what a frame is named by after its owner
    private readonly Dictionary<float, int> framesOpened = [];

    internal UIFrameList FrameList => frameList;

    // Helper method to start the frames over with the list, the first one the whole of it unmoved
    private void BeginFrames()
    {
        frameList.Clear();
        openFrames.Clear();
        openClips.Clear();
        framesOpened.Clear();
        shift = Vector2.Zero;
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
    public void PushFrame(Vector2 moved)
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
        int opened = framesOpened.GetValueOrDefault(owner);
        framesOpened[owner] = opened + 1;
        frameList.Frames.Add(new UIFrame(new Vector2(owner, -1 - opened), frameAt, shift, visualScale, visualOffset, scale, origin));
        frameAt = frameList.Frames.Count - 1;
    }

    // Helper method for a visual pushed, its own frame inside of the one it's in
    private void PushVisualFrame()
    {
        openFrames.Push((frameAt, clipAt, shift));
        OpenFrame();
    }

    private void PopVisualFrame() => (frameAt, _, _) = openFrames.Pop();

    // Helper method for a module moved and scaled, a frame of its own at the top
    private void TransformFrame()
    {
        frameAt = -1;
        OpenFrame();
    }

    private void PushFrameClip(UIRect rect)
    {
        openClips.Push(clipAt);
        frameList.Clips.Add(new UIFrameClip(rect, frameAt, clipAt));
        clipAt = frameList.Clips.Count - 1;
    }

    private void PopFrameClip() => clipAt = openClips.Pop();

    // Helper method to keep a quad as it was asked for, see Quad
    private void KeepFrameQuad(UIRect rect, Vector2 texTopLeft, Vector2 texBottomRight, uint color, uint flags, float ring, float slant, float upright)
    {
        frameList.Add() = new UIFrameQuad
        {
            Rect = rect,
            TexMin = texTopLeft,
            TexMax = texBottomRight,
            Color = color,
            Flags = flags,
            Ring = ring,
            Slant = slant,
            Upright = upright,
            Key = new Vector2(stepped ? -owner : owner, ownerPainted),
            Frame = frameAt,
            Clip = clipAt
        };
    }

    // Helper method for the last quad kept turned about a point of the layout, see Icon
    private void TurnFrameQuad(float turn, Vector2 pivot)
    {
        ref UIFrameQuad quad = ref frameList.Quads[frameList.QuadCount - 1];
        quad.Turn = turn;
        quad.Pivot = pivot;
    }

    private void KeepFrameGlass(UIRect rect, float radius, float blur, float frosted, float bend) =>
        frameList.Glass.Add(new UIFrameGlass(frameList.QuadCount, rect, frameAt, clipAt, radius, blur, frosted, bend));

    private void KeepFrameRun(Texture? image0, Texture? image1) => frameList.Runs.Add(new UIFrameRun(frameList.QuadCount, image0, image1));

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
            if (a.Start != b.Start || !Near(a.Min, b.Min) || !Near(a.Max, b.Max) || !Near(a.ClipMin, b.ClipMin) || !Near(a.ClipMax, b.ClipMax) ||
                a.Radius != b.Radius || a.Blur != b.Blur || a.Opacity != b.Opacity || a.Bend != b.Bend)
                return $"pane of glass {i} differs, resolved {a}, painted {b}";
        }

        return null;

        static bool Near(Vector2 a, Vector2 b) => a == b || Vector2.Distance(a, b) < 0.01f;
    }
}
