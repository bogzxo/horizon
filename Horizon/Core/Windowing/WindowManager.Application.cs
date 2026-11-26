using System.Diagnostics;

using Horizon.Platform;

namespace Horizon.Core;

// A window run as an application (RunMode.Application) rather than a game, an editor that sits there doing nothing
// most of the time. It draws a frame only when something wants one, ticks a few times a second while nothing is
// happening, and its thread waits for the system instead of asking it a thousand times a second, see
// ApplicationBehaviour. A game has none of it unless it asks, every frame the screen shows, every tick at the rate.
// How the platform's thread waits is the host's, what it waits for and when frames and ticks are due is decided here.
public partial class WindowManager
{
    // How long after the last input a window counts as idle, and how many ticks without a UI changing.
    // Half a second bridges pauses between wheel notches, keystrokes and menu clicks without delaying deep idle sleep.
    // In Low Power Mode / Battery Saver, return to idle twice as fast after a burst.
    internal const double IDLE_AFTER = 0.5;
    internal const double IDLE_AFTER_LOW_POWER = 0.25;
    private const long IDLE_TICKS = 2;

    internal static double IdleThreshold(bool lowPowerMode) => lowPowerMode ? IDLE_AFTER_LOW_POWER : IDLE_AFTER;

    // How long frames are drawn after input or a change, enough for the blend between two ticks and a refresh more
    private const double FRAMES_AFTER = 0.05;

    // The frame drawn a second however still everything is, so nothing anybody forgot to ask for stays stale for long
    private const int HEARTBEAT_MS = 1000;


    // Quiet windows sleep up to the longest an idle tick can stretch (SimulationLoop.MAX_STRETCH), so timers
    // still keep time. Carets request an earlier tick for their next blink; input and queued work wake immediately.
    private const double IDLE_PERIOD = 0.25;

    // The same minimised or hidden, where a caret's blink or a spinner is seen by nobody. Their early ticks aren't taken
    // either (see RequestTickIn), timers keep slower time
    private const double IDLE_PERIOD_MINIMISED = 2.0;

    // How long frames are still drawn after the window is minimised, enough for the one that sizes everything down
    private const double FRAMES_MINIMISED = 0.25;

    // Whether the window has the keyboard and whether it's minimised, kept by the window's thread, and when it was minimised
    private volatile bool windowFocused = true, windowMinimised;
    private long minimisedAt;

    /// <summary>Whether the window has the keyboard, for whatever only moves for somebody looking (a caret's blink). From any thread.</summary>
    public bool IsFocused => windowFocused;

    /// <summary>Whether the window is down to the taskbar, or the app is in the background on a phone. A fight pauses on it. From any thread.</summary>
    public bool IsMinimized => windowMinimised;

    // Asleep with the window hidden, see Dormant
    private volatile bool dormant;

    // The longest the simulation sleeps while dormant, an hour, Wake is what it's waiting for
    private const double IDLE_PERIOD_DORMANT = 3600.0;

    /// <summary>
    /// Whether the program is asleep with its window hidden, see <see cref="Sleep"/> and <see cref="Resume"/>. The
    /// simulation doesn't tick, no frames are drawn, not even the heartbeat, and the window's thread waits on the system.
    /// From any thread.
    /// </summary>
    public bool Dormant
    {
        get => dormant;
        private set
        {
            dormant = value;
            UpdateIdlePeriod();
            host.Window?.WakeWindowThread();
            Wake();
        }
    }

    /// <summary>What the window is run for, see <see cref="WindowManagerConfiguration.Mode"/>.</summary>
    public RunMode Mode { get; }

    /// <summary>Which of the ways of saving work while nothing is happening are on, see <see cref="ApplicationBehaviour"/>.</summary>
    public ApplicationBehaviour Behaviour { get; }

    // The last input (stopwatch ticks), the last tick a UI painted something new on, and until when frames are drawn
    private long lastInput, lastChangeTick, drawUntil;
    private readonly AutoResetEvent frameWanted = new(false);
    // Wakes PaceCalmFrames immediately when the window leaves calm mode (input, RequestSmooth, exclusive/lifecycle work, or stop)
    private readonly AutoResetEvent calmWake = new(false);

    /// <summary>
    /// Monotonically extends a stopwatch-tick deadline to at least <paramref name="now"/> + <paramref name="seconds"/>.
    /// Returns true when the deadline was already expired (transitioning from inactive to active).
    /// </summary>
    internal static bool ExtendDeadline(ref long deadline, long now, double seconds)
    {
        bool wasActive = now < Volatile.Read(ref deadline);
        long until = now + (long)(seconds * Stopwatch.Frequency);
        long had;
        do
        {
            had = Volatile.Read(ref deadline);
            if (had >= until)
                break;
        } while (Interlocked.CompareExchange(ref deadline, until, had) != had);

        return !wasActive;
    }

    /// <summary>
    /// Asks for frames to be drawn for a while, for a window that only draws when something wants it to
    /// (<see cref="ApplicationBehaviour.DrawOnChange"/>). Nothing for one that draws every frame anyway. From any thread.
    /// </summary>
    public void RequestFrames(double seconds = FRAMES_AFTER)
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        ExtendDeadline(ref drawUntil, Stopwatch.GetTimestamp(), seconds);
        frameWanted.Set();
    }

    /// <summary>
    /// Says that what is on screen changed (a UI painted something new), which keeps the simulation at its full rate and
    /// has frames drawn to show it. The compositors say so by themselves. From the simulation thread.
    /// </summary>
    public void NoteChange()
    {
        Volatile.Write(ref lastChangeTick, Tick);

        // While calm, keep drawing for a full calm period (2x PaceCalmFrames' half-period) so a change never expires
        // before PaceCalmFrames lets the frame through and UIInBetween finishes blending cleanly without stutter.
        RequestFrames(CalmFrameWindow(CalmNow()));
    }

    internal static double CalmFrameWindow(double calm) => calm > 0.0 ? calm : FRAMES_AFTER;

    // Until when something moving asked for the full rate, see RequestSmooth
    private long smoothUntil;

    // Until when interactive pointer-following drag asked to bypass WaitForFrame pacing, see RequestUncappedFrames
    private long uncappedUntil;

    /// <summary>
    /// Asks for the full rate of ticks and frames for a while, for something moving on its own that a person follows with
    /// their eye: a glide after a flick, a spring going back. Without it, those were drawn at the calm rate once the input
    /// that started them was old (see CalmNow), smooth under the fingers and choppy after. Text coming
    /// in, a spinner, a clock don't ask, they're fine at the calm rate. From any thread, every tick it goes on moving.
    /// </summary>
    public void RequestSmooth(double seconds = FRAMES_AFTER)
    {
        // When transitioning from calm/idle into smooth, wake both loops immediately so the animation
        // never stalls waiting out a 41 ms / 100 ms calm sleep.
        if (ExtendDeadline(ref smoothUntil, Stopwatch.GetTimestamp(), seconds))
        {
            simulation?.Wake();
            calmWake.Set();
        }

        RequestFrames(seconds);
    }

    /// <summary>
    /// Temporarily bypasses <see cref="WaitForFrame"/> frame-rate capping while interactive pointer-following motion
    /// (such as dragging a floating window) is active, so Mailbox presentation can deliver the freshest cursor position
    /// up to vblank. Also keeps smooth pacing active for the same window. From any thread.
    /// </summary>
    public void RequestUncappedFrames(double seconds = FRAMES_AFTER)
    {
        ExtendDeadline(ref uncappedUntil, Stopwatch.GetTimestamp(), seconds);
        RequestSmooth(seconds);
    }

    /// <summary>
    /// Wakes an application that went quiet, from any thread, for work that finished somewhere else (a search, a file
    /// read, a program run off the simulation's thread) and is waiting to be taken in on the next tick. Without it the
    /// result would wait for the next slow tick of an idle simulation.
    /// </summary>
    public void Wake()
    {
        simulation?.Wake();
        RequestFrames();
    }

    /// <summary>Schedules the next timed UI change without requesting unchanged frames. Simulation thread only.</summary>
    public void RequestTickIn(double seconds)
    {
        if ((Behaviour & ApplicationBehaviour.SlowWhenIdle) != 0 && !windowMinimised && !dormant)
            simulation?.RequestTickIn(seconds);
    }

    /// <summary>
    /// Records interactive input (platform pointer/keyboard, IME, key repeat, or interactive text edits), keeping the
    /// window at its full rate for <see cref="IDLE_AFTER"/> seconds and waking the simulation and render loops
    /// immediately without holding the render loop awake past <see cref="FRAMES_AFTER"/>. From any thread.
    /// </summary>
    public void NoteInput()
    {
        // A game was never told about input this way, it has nothing to wake
        if (Behaviour == ApplicationBehaviour.None)
            return;

        Volatile.Write(ref lastInput, Stopwatch.GetTimestamp());
        simulation?.Wake();
        calmWake.Set();
        RequestFrames();

        // What the input changes is shown the moment its tick is done, see LowLatency
        RequestUrgentFrames();
    }

    private bool InputLately =>
        Stopwatch.GetTimestamp() - Volatile.Read(ref lastInput) < (long)(IdleThreshold(PlatformPower.IsLowPowerMode) * Stopwatch.Frequency);

    private bool SmoothNow => Stopwatch.GetTimestamp() < Volatile.Read(ref smoothUntil);

    internal bool UncappedFramesNow => UncappedFramesFor(Stopwatch.GetTimestamp(), Volatile.Read(ref uncappedUntil));

    internal static bool UncappedFramesFor(long now, long uncappedUntilStamp) => now < uncappedUntilStamp;

    /// <summary>Whether nothing is happening, no input lately, no active smooth motion, no UI changed for a couple of ticks, nothing waiting to be done.</summary>
    private bool IdleNow() =>
        !InputLately && !SmoothNow && Tick - Volatile.Read(ref lastChangeTick) > IDLE_TICKS && !exclusiveRequested && !EntityLifecycle.HasWork;

    // How far apart ticks are while the screen changes with nobody touching it: a film's 24 a second in front (the lowest a
    // Mac's ProMotion goes for motion, and the frames between two ticks blend what moves), 10 behind other windows. In Low
    // Power Mode / Battery Saver, drop calm work further to 12 Hz focused and 4 Hz unfocused while keeping input & smooth at full rate.
    internal const double CALM_PERIOD = 1.0 / 24.0, CALM_PERIOD_BEHIND = 1.0 / 10.0;
    internal const double CALM_PERIOD_LOW_POWER = 1.0 / 12.0, CALM_PERIOD_BEHIND_LOW_POWER = 1.0 / 4.0;

    /// <summary>
    /// How long the next tick may wait while something changes on screen that nobody is touching, an agent's chat streaming
    /// in, 0 for the full rate. Every one of a hundred and twenty ticks a second painted all of the UI again for text that
    /// came a few times a second, which was a Mac run hot through a long agent session. It follows how often the screen
    /// really changes (NoteChange), down to 24 a second in front and 10 behind other windows (12 / 4 in Low Power Mode).
    /// Input, an animation asking for ticks (RequestSmooth) and work waiting have the full rate at once.
    /// </summary>
    internal double CalmNow() => CalmPeriodFor(
        Stopwatch.GetTimestamp(),
        Volatile.Read(ref lastInput),
        Volatile.Read(ref smoothUntil),
        exclusiveRequested,
        EntityLifecycle.HasWork,
        windowMinimised,
        windowFocused,
        PlatformPower.IsLowPowerMode);

    internal static double CalmPeriodFor(
        long now,
        long lastInputStamp,
        long smoothUntilStamp,
        bool exclusive,
        bool lifecycleWork,
        bool minimized,
        bool focused) =>
        CalmPeriodFor(now, lastInputStamp, smoothUntilStamp, exclusive, lifecycleWork, minimized, focused, lowPowerMode: false);

    internal static double CalmPeriodFor(
        long now,
        long lastInputStamp,
        long smoothUntilStamp,
        bool exclusive,
        bool lifecycleWork,
        bool minimized,
        bool focused,
        bool lowPowerMode)
    {
        if (now - lastInputStamp < (long)(IdleThreshold(lowPowerMode) * Stopwatch.Frequency)
            || now < smoothUntilStamp
            || exclusive
            || lifecycleWork
            || minimized)
            return 0.0;

        if (lowPowerMode)
            return focused ? CALM_PERIOD_LOW_POWER : CALM_PERIOD_BEHIND_LOW_POWER;

        return focused ? CALM_PERIOD : CALM_PERIOD_BEHIND;
    }

    /// <summary>
    /// Helper method to take in what the window was like as it came up. Creation can finish without a focus transition
    /// (Windows refusing to activate a new background window, say), so the cached state starts from what the native
    /// window said rather than relying on later events. An application only, a game was never told. Platform thread, once.
    /// </summary>
    private void TakeState(Horizon.Platform.PlatformState state)
    {
        platformDisplay = state.Display;
        display = new DisplayState(state.Display.ViewportSize, state.Display.WindowSize, state.Display.ScreenSize);

        if (Behaviour == ApplicationBehaviour.None)
            return;

        windowFocused = state.Focused;
        windowMinimised = state.Minimized;
    }

    /// <summary>Helper method for the window getting the keyboard or losing it. A game ignores it. Platform thread.</summary>
    private void OnFocus(bool focused)
    {
        if (Behaviour == ApplicationBehaviour.None)
            return;

        windowFocused = focused;
        UpdateIdlePeriod();
        NoteInput();
    }

    /// <summary>
    /// Helper method for the window going down to the taskbar or coming back, or the app going to the back on a phone.
    /// The state is kept whatever the behaviour (a game pauses its fight on IsMinimized), only an application holds its
    /// frames on it. Platform thread.
    /// </summary>
    private void OnMinimized(bool minimised)
    {
        if (minimised && !windowMinimised)
            Volatile.Write(ref minimisedAt, Stopwatch.GetTimestamp());
        windowMinimised = minimised;
        if (Behaviour == ApplicationBehaviour.None)
            return;

        UpdateIdlePeriod();
        NoteInput();
    }

    /// <summary>
    /// Whether nothing is going on that the platform's thread has to look out for, so it may wait for the system to have
    /// something to say rather than look a thousand times a second. Whether it can wait at all, and whether it's in the
    /// middle of something of its own (a drag, a slide), is the host's to know.
    /// </summary>
    private bool MayWaitForEvents => (Behaviour & ApplicationBehaviour.WaitForEvents) != 0 && !InputLately && !SmoothNow;

    internal bool IsMayWaitForEvents => MayWaitForEvents;

    internal static bool MayWaitForEventsFor(
        ApplicationBehaviour behaviour,
        long now,
        long lastInputStamp,
        long smoothUntilStamp,
        bool lowPowerMode = false) =>
        (behaviour & ApplicationBehaviour.WaitForEvents) != 0
        && now - lastInputStamp >= (long)(IdleThreshold(lowPowerMode) * Stopwatch.Frequency)
        && now >= smoothUntilStamp;

    /// <summary>
    /// Sets the maximum idle sleep. Timed UI changes can request an earlier tick.
    /// An application only, a self test run as a game keeps its pace. Any thread.
    /// </summary>
    private void UpdateIdlePeriod()
    {
        if (simulation is not { } loop || Mode != RunMode.Application)
            return;

        // Minimised or hidden, nothing on screen, it looks in now and then. What wakes it (input, queued work, Wake)
        // still does at once
        loop.IdlePeriod = dormant ? IDLE_PERIOD_DORMANT : windowMinimised ? IDLE_PERIOD_MINIMISED : IDLE_PERIOD;
    }

    /// <summary>
    /// Whether frames are held back because the window is dormant, or minimised and has been for a moment, whatever painted
    /// something new. There's nothing to see, and the window has been sized down already. An application only.
    /// </summary>
    private bool Hidden => IsHidden(Mode, dormant, windowMinimised, Stopwatch.GetTimestamp(), Volatile.Read(ref minimisedAt));

    internal static bool IsHidden(RunMode mode, bool isDormant, bool minimized, long now, long minimizedAtStamp) =>
        mode == RunMode.Application
        && (isDormant || (minimized && now - minimizedAtStamp > (long)(FRAMES_MINIMISED * Stopwatch.Frequency)));

    /// <summary>
    /// Helper method to hold the thread that draws until a frame is wanted, or a second has gone by. Nothing for a window
    /// that draws every frame. Minimised, only for work that waits on a frame to be done (a scene swapped, something
    /// added to be set up), not for what painted something new or the heartbeat. The thread that draws, before every frame.
    /// </summary>
    private void WaitForDemand()
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        while (!renderStopping)
        {
            bool hidden = Hidden;
            // The surface going and coming back is done between frames, asleep as well (see Sleep)
            if (!surfaceRequests.IsEmpty)
                return;

            if (hidden ? exclusiveRequested || EntityLifecycle.HasWork : Stopwatch.GetTimestamp() < Volatile.Read(ref drawUntil))
                return;

            // Dormant there's no heartbeat either, only somebody asking for a frame wakes it
            if (!frameWanted.WaitOne(hidden && dormant ? Timeout.Infinite : HEARTBEAT_MS) && !hidden)
                return;
        }
    }

    // When the last frame was let through while calm, see PaceCalmFrames
    private long calmFrameAt;

    /// <summary>
    /// While calm (see CalmNow), frames no closer together than half a calm tick: the frames between two ticks still blend
    /// what moves, at twice the ticks' rate rather than the screen's. A spinner on screen asked for frames for ever, and
    /// with them every frame of the screen's 120 a second put the whole UI together and up again. Input, or anybody waking
    /// the frames, has them at once. The thread that draws, after WaitForDemand.
    /// </summary>
    private void PaceCalmFrames()
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        double calm = CalmNow();
        long now = Stopwatch.GetTimestamp();
        if (calm > 0.0)
        {
            // Event-driven wait up to half a calm tick; breaks out immediately if CalmNow() drops to 0
            // (input, RequestSmooth, exclusiveRequested, EntityLifecycle.HasWork) or renderStopping is set.
            long until = calmFrameAt + (long)(calm * 0.5 * Stopwatch.Frequency);
            while (!renderStopping && now < until && CalmNow() > 0.0)
            {
                int ms = (int)Math.Ceiling((until - now) * 1000.0 / Stopwatch.Frequency);
                if (ms <= 0)
                    break;
                calmWake.WaitOne(ms);
                now = Stopwatch.GetTimestamp();
            }
        }

        calmFrameAt = now;
    }
}
