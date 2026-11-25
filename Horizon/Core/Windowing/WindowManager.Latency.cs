using System.Diagnostics;

using Horizon.Core.Threading;
using Horizon.Logging;

namespace Horizon.Core;

// The least time between a hand moving and the screen showing it, for a window that is somebody's tool rather than a
// game's picture. Three things stand between the two: the frame that waits for the newest tick to have a tick after it
// to blend with (PresentationMode.Interpolated, a tick late), the limit on the frames, which has a frame start whenever
// its turn comes round rather than when there is something new to show, and a UI that is put together anew only so
// many times a second. With LowLatency on the newest tick is shown as it is, and for as long as somebody asks
// (RequestUrgentFrames, which input and a window being dragged do by themselves) frames come at UrgentFramesPerSecond
// instead, so what is new is on its way to the screen within a couple of milliseconds. A screen that takes the newest
// frame it was handed (mailbox, what vsync is here wherever the driver has it) throws the others away, so nothing
// tears. It costs the frames that are thrown away, only while the hand is moving.
public partial class WindowManager
{
    /// <summary>How many frames a second are drawn while they're urgent, unless said otherwise.</summary>
    public const double URGENT_FRAMES_PER_SECOND = 480.0;

    private volatile bool lowLatency;
    private long urgentUntil;
    private double urgentPeriod = 1.0 / URGENT_FRAMES_PER_SECOND;

    /// <summary>
    /// Whether what a person does gets to the screen as soon as it can rather than as smoothly and cheaply as it can.
    /// On, frames show the newest tick whatever <see cref="Presentation"/> says, and frames that are urgent
    /// (<see cref="RequestUrgentFrames"/>) are drawn at <see cref="UrgentFramesPerSecond"/> rather than at the limit
    /// of the display's settings. Off unless it's switched on. From any thread, it takes hold at the next frame.
    /// </summary>
    public bool LowLatency
    {
        get => lowLatency;
        set
        {
            if (lowLatency == value)
                return;

            lowLatency = value;
            Log.Info($"[{Name}] Low latency is {(value ? "on" : "off")}.");
            Wake();
        }
    }

    /// <summary>
    /// The most frames a second while they're urgent and <see cref="LowLatency"/> is on, 0 for as many as there is
    /// time for. Never fewer than the display's own limit allows. From any thread.
    /// </summary>
    public double UrgentFramesPerSecond
    {
        get => urgentPeriod > 0.0 ? 1.0 / urgentPeriod : 0.0;
        set => urgentPeriod = PeriodOf(value);
    }

    /// <summary>
    /// Asks for frames as fast as <see cref="UrgentFramesPerSecond"/> for a while, for something that follows a hand
    /// (a window being dragged, a slider, a stroke of a pen). Nothing unless <see cref="LowLatency"/> is on. Input
    /// asks by itself. From any thread, for as long as it goes on.
    /// </summary>
    public void RequestUrgentFrames(double seconds = FRAMES_AFTER)
    {
        if (!lowLatency)
            return;

        ExtendDeadline(ref urgentUntil, Stopwatch.GetTimestamp(), seconds);
        calmWake.Set();
        RequestFrames(seconds);
    }

    /// <summary>Whether frames are urgent right now, see <see cref="RequestUrgentFrames"/>. From any thread.</summary>
    public bool UrgentNow => lowLatency && Stopwatch.GetTimestamp() < Volatile.Read(ref urgentUntil);

    // How frames show the simulation this frame, the newest tick while low latency is on
    private PresentationMode PresentationNow => lowLatency ? PresentationMode.Latest : Presentation;

    /// <summary>
    /// Helper method to say how long there is between two frames at the least, the display's limit or the shorter one
    /// of frames that are urgent. 0 for no limit.
    /// </summary>
    /// <param name="period">What the display's settings say, 0 for no limit.</param>
    /// <param name="urgentPeriod">What urgent frames are held to, 0 for no limit.</param>
    /// <param name="urgent">Whether frames are urgent.</param>
    internal static double FramePeriodFor(double period, double urgentPeriod, bool urgent) =>
        !urgent || period <= 0.0 ? period : Math.Min(period, urgentPeriod);
}
