using System.Diagnostics;

namespace Horizon.Core;

// A window run as an application (RunMode.Application) rather than a game, an editor that sits there doing nothing
// most of the time. It draws a frame only when something wants one, ticks a few times a second while nothing is
// happening, and its thread waits for the system instead of asking it a thousand times a second, see
// ApplicationBehaviour. A game has none of it unless it asks, every frame the screen shows, every tick at the rate.
// How the platform's thread waits is the host's, what it waits for and when frames and ticks are due is decided here.
public partial class WindowManager
{
    // How long after the last input a window counts as idle, and how many ticks without a UI changing
    private const double IDLE_AFTER = 0.25;
    private const long IDLE_TICKS = 2;

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


    /// <summary>
    /// Asks for frames to be drawn for a while, for a window that only draws when something wants it to
    /// (<see cref="ApplicationBehaviour.DrawOnChange"/>). Nothing for one that draws every frame anyway. From any thread.
    /// </summary>
    public void RequestFrames(double seconds = FRAMES_AFTER)
    {
        if ((Behaviour & ApplicationBehaviour.DrawOnChange) == 0)
            return;

        long until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long had;
        do
        {
            had = Volatile.Read(ref drawUntil);
            if (had >= until)
                break;
        } while (Interlocked.CompareExchange(ref drawUntil, until, had) != had);

        frameWanted.Set();
    }

    /// <summary>
    /// Says that what is on screen changed (a UI painted something new), which keeps the simulation at its full rate and
    /// has frames drawn to show it. The compositors say so by themselves. From the simulation thread.
    /// </summary>
    public void NoteChange()
    {
        Volatile.Write(ref lastChangeTick, Tick);

        // Calm, a change is shown in a frame or two, not the stretch of frames input wants for its blend: presented as often
        // as the screen changes, a display that can (a Mac's ProMotion) goes down to that rate, and so does the GPU
        RequestFrames(CalmNow() > 0.0 ? CALM_FRAMES : FRAMES_AFTER);
    }

    // How long frames are drawn after a change while calm, two at 120 Hz
    private const double CALM_FRAMES = 1.0 / 60.0;

    // Until when something moving asked for the full rate, see RequestSmooth
    private long smoothUntil;

    /// <summary>
    /// Asks for the full rate of ticks and frames for a while, for something moving on its own that a person follows with
    /// their eye: a glide after a flick, a spring going back. Without it, those were drawn at the calm rate once the input
    /// that started them was a quarter of a second old (see CalmNow), smooth under the fingers and choppy after. Text coming
    /// in, a spinner, a clock don't ask, they're fine at the calm rate. From any thread, every tick it goes on moving.
    /// </summary>
    public void RequestSmooth(double seconds = FRAMES_AFTER)
    {
        long until = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long had;
        do
        {
            had = Volatile.Read(ref smoothUntil);
            if (had >= until)
                break;
        } while (Interlocked.CompareExchange(ref smoothUntil, until, had) != had);

        RequestFrames(seconds);
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
        if ((Behaviour & ApplicationBehaviour.SlowWhenIdle) != 0 && !windowMinimised)
            simulation?.RequestTickIn(seconds);
    }

    /// <summary>Helper method for input of any kind, which wakes everything up. Thread of the window.</summary>
    private void NoteInput()
    {
        // A game was never told about input this way, it has nothing to wake
        if (Behaviour == ApplicationBehaviour.None)
            return;

        Volatile.Write(ref lastInput, Stopwatch.GetTimestamp());
        simulation?.Wake();
        RequestFrames();
    }

    private bool InputLately => Stopwatch.GetTimestamp() - Volatile.Read(ref lastInput) < (long)(IDLE_AFTER * Stopwatch.Frequency);

    /// <summary>Whether nothing is happening, no input lately, no UI changed for a couple of ticks, nothing waiting to be done.</summary>
    private bool IdleNow() =>
        !InputLately && Tick - Volatile.Read(ref lastChangeTick) > IDLE_TICKS && !exclusiveRequested && !EntityLifecycle.HasWork;

    // How far apart ticks are while the screen changes with nobody touching it: a film's 24 a second in front (the lowest a
    // Mac's ProMotion goes for motion, and the frames between two ticks blend what moves), 10 behind other windows. The
    // frames are only as many as the changes (NoteChange), so a display that varies its rate goes down with them
    private const double CALM_PERIOD = 1.0 / 24.0, CALM_PERIOD_BEHIND = 1.0 / 10.0;

    /// <summary>
    /// How long the next tick may wait while something changes on screen that nobody is touching, an agent's chat streaming
    /// in, 0 for the full rate. Every one of a hundred and twenty ticks a second painted all of the UI again for text that
    /// came a few times a second, which was a Mac run hot through a long agent session. It follows how often the screen
    /// really changes (NoteChange), down to 24 a second in front and 10 behind other windows. Input, an animation asking
    /// for ticks (Wake) and work waiting have the full rate at once.
    /// </summary>
    private double CalmNow()
    {
        if (InputLately || exclusiveRequested || EntityLifecycle.HasWork || windowMinimised
            || Stopwatch.GetTimestamp() < Volatile.Read(ref smoothUntil))
            return 0.0;

        return windowFocused ? CALM_PERIOD : CALM_PERIOD_BEHIND;
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
    private bool MayWaitForEvents => (Behaviour & ApplicationBehaviour.WaitForEvents) != 0 && !InputLately;

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
    /// Whether frames are held back because the window is minimised and has been for a moment, whatever painted
    /// something new. There's nothing to see, and the window has been sized down already. An application only.
    /// </summary>
    private bool Hidden => Mode == RunMode.Application && windowMinimised &&
        Stopwatch.GetTimestamp() - Volatile.Read(ref minimisedAt) > (long)(FRAMES_MINIMISED * Stopwatch.Frequency);

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
            // In short sleeps, so input coming has its frame at once. Frames asked for don't end it, a spinner asks every paint
            long until = calmFrameAt + (long)(calm * 0.5 * Stopwatch.Frequency);
            while (!renderStopping && !InputLately && Stopwatch.GetTimestamp() < until)
                Thread.Sleep(Math.Clamp((int)((until - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency), 1, 4));
            now = Stopwatch.GetTimestamp();
        }

        calmFrameAt = now;
    }
}
