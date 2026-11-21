using System.Diagnostics;

namespace Horizon.Core.Diagnostics;

/// <summary>
/// What every frame drawn showed, kept while somebody asks for it: when it went out, which scene was drawn (none on a
/// frame that drew no scene at all), and what a UI that says what it is (see <c>UICompositor.TraceTag</c>) put on it,
/// the tag it was painted with, the skin and theme it was painted in, and whether it drew nothing. For a test that wants
/// to know a change on screen went out whole, no frame empty and no frame of one thing painted in another's colours,
/// and how long it took. Off (the usual) it costs a branch on the render thread.
/// </summary>
public static class FrameTrace
{
    /// <summary>One frame that went out.</summary>
    /// <param name="At">When it was drawn, against <see cref="Stopwatch"/>.</param>
    /// <param name="Scene">The scene drawn, null when none was.</param>
    /// <param name="Tag">What the traced UI was painted as, null when it drew nothing of its own this frame.</param>
    /// <param name="Skin">The skin's file and theme the traced UI was painted in.</param>
    /// <param name="Empty">Whether the traced UI drew nothing at all, a frame of the ground and no UI.</param>
    /// <param name="Painted">How many times the traced UI had been painted by the list it drew from.</param>
    /// <param name="Cover">How far a scene transition covered the frame, 0 for not at all, 1 for all of it.</param>
    public readonly record struct Frame(long At, string? Scene, object? Tag, string? Skin, bool Empty, long Painted, float Cover);

    private const int MOST = 8192;

    private static readonly Lock gate = new();
    private static readonly List<Frame> frames = [];

    // What the traced UI said this frame, until the scene manager closes the frame
    private static object? tag;
    private static string? skin;
    private static bool empty, drawn;
    private static long painted;

    /// <summary>Whether frames are kept. From any thread.</summary>
    public static volatile bool On;

    /// <summary>A traced UI's part of the frame being drawn. Render thread.</summary>
    public static void Ui(object? paintedAs, string? skinName, bool nothing, long paintedFrame)
    {
        if (!On) return;

        tag = paintedAs;
        skin = skinName;
        empty = nothing;
        painted = paintedFrame;
        drawn = true;
    }

    /// <summary>The frame done, with the scene that was drawn in it (null for none) and how far it was covered. Render thread.</summary>
    public static void End(string? scene, float cover)
    {
        if (!On) return;

        var frame = new Frame(Stopwatch.GetTimestamp(), scene, drawn ? tag : null, drawn ? skin : null, drawn && empty, drawn ? painted : -1, cover);
        drawn = false;
        lock (gate)
        {
            if (frames.Count >= MOST)
                frames.RemoveRange(0, MOST / 2);
            frames.Add(frame);
        }
    }

    /// <summary>Every frame kept since the last time, handed over and forgotten. From any thread.</summary>
    public static Frame[] Take()
    {
        lock (gate)
        {
            Frame[] all = [.. frames];
            frames.Clear();
            return all;
        }
    }
}
