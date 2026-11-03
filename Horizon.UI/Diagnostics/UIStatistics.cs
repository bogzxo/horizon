using System.Diagnostics;

using Horizon.Graphics;

namespace Horizon.UI.Diagnostics;

/// <summary>
/// What a UI cost, added up from the moment it was last <see cref="Reset"/>. The ticks count on the simulation thread
/// (laying out and painting, and the look at whether anything changed), the frames on the render thread (putting a
/// frame between two ticks together, what went up to the GPU and how long the GPU took). For measuring the UI with a
/// run of the same thing before and after a change, read between runs, a count can be a tick out either side.
/// </summary>
public sealed class UIStatistics
{
    /// <summary>The names the UI's stretches of a frame go by in <see cref="GraphicsDevice.GpuScopes"/>.</summary>
    public const string GPU_SCOPE = "ui", GLASS_SCOPE = "ui glass";

    /// <summary>Ticks painted, and how many of them painted something other than the tick before.</summary>
    public long Ticks, TicksChanged;

    /// <summary>Milliseconds laying out and painting, and looking at whether the list changed.</summary>
    public double PaintMs, CompareMs;

    /// <summary>Quads painted, all ticks together, and the most one tick painted.</summary>
    public long QuadsPainted;
    public int MostQuads;

    /// <summary>Frames drawn, how many of them put a frame between two ticks together, and how many uploaded quads.</summary>
    public long Frames, FramesBlended, FramesUploaded;

    /// <summary>Milliseconds putting frames between two ticks together.</summary>
    public double BlendMs;

    /// <summary>Quads and bytes uploaded for the UI.</summary>
    public long QuadsUploaded, BytesUploaded;

    /// <summary>Milliseconds the GPU spent drawing the UI (the glass included), and on the glass alone.</summary>
    public double GpuMs, GlassGpuMs;

    /// <summary>Starts counting from nothing again.</summary>
    public void Reset()
    {
        Ticks = TicksChanged = QuadsPainted = Frames = FramesBlended = FramesUploaded = QuadsUploaded = BytesUploaded = 0;
        MostQuads = 0;
        PaintMs = CompareMs = BlendMs = GpuMs = GlassGpuMs = 0.0;
    }

    /// <summary>One line of all of it, averaged where an average says more than a sum.</summary>
    public override string ToString()
    {
        double ticks = Math.Max(1, Ticks), frames = Math.Max(1, Frames);
        return $"{Ticks} ticks ({TicksChanged} changed), paint {PaintMs / ticks:0.000} ms, compare {CompareMs / ticks:0.000} ms, " +
            $"{QuadsPainted / ticks:0} quads a tick (most {MostQuads}), {Frames} frames ({FramesBlended} blended, {FramesUploaded} uploaded), " +
            $"blend {BlendMs / frames:0.000} ms, {QuadsUploaded} quads and {BytesUploaded / 1024.0:0} KB up, " +
            $"GPU {GpuMs / frames:0.000} ms a frame (glass {GlassGpuMs / frames:0.000})";
    }

    internal static double Since(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>Helper method to add what the GPU said the UI's last finished frame took. Render thread.</summary>
    internal void CountGpu()
    {
        if (!GraphicsDevice.IsAvailable || GraphicsDevice.Current is not { } device)
            return;

        foreach (GpuScope scope in device.GpuScopes)
        {
            if (ReferenceEquals(scope.Name, GPU_SCOPE)) GpuMs += scope.Milliseconds;
            else if (ReferenceEquals(scope.Name, GLASS_SCOPE)) GlassGpuMs += scope.Milliseconds;
        }
    }
}
