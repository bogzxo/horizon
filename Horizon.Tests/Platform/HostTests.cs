using System.Numerics;

using Horizon.Audio;
using Horizon.Core;
using Horizon.Input;
using Horizon.Platform;

namespace Horizon.Tests.Platform;

/// <summary>
/// The engine on a host that isn't a desktop, one with no window at all, the way a phone's will be. Nothing here makes a
/// GPU, the window manager is made, told things by its host and let go of again.
/// </summary>
public class HostTests
{
    private static readonly WindowManagerConfiguration Config = WindowManagerConfiguration.Default1600x900 with { CustomFrame = true };

    [Fact]
    public void A_host_without_a_window_is_a_valid_window_manager()
    {
        var host = new FakeHost();
        var window = new WindowManager(Config, host);

        Assert.Same(host, window.Host);
        Assert.Equal(PlatformFeatures.None, window.Features);

        // Asked for a frame of its own, but there's no window to have one, so nothing pretends to
        Assert.False(window.CustomFrame);
        Assert.False(window.IsMaximized);
        Assert.False(window.IsDropped);
        Assert.Equal(-1, window.RegisterHotKey("Ctrl+`"));
        Assert.False(window.TakeHotKey(1));
        window.Minimize();
        window.ToggleMaximized();
        window.BeginMove();
        window.BeginResize(WindowEdges.Left);
        window.SetCornerRadius(8.0f);
        window.SetDragArea(new System.Drawing.RectangleF(0, 0, 10, 10), []);
        window.MaximizeButton = new System.Drawing.RectangleF(1, 1, 2, 2);
        Assert.True(window.MaximizeButton.IsEmpty);
        window.Drop(true);
        window.BringToFront();
        window.Title = "phone";
        Assert.Equal("phone", window.Title);

        window.Dispose();
        Assert.Equal(1, host.Disposals);
    }

    [Fact]
    public void The_host_is_attached_as_the_window_manager_is_made_and_closing_is_the_hosts()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.Equal(1, host.Attaches);
        Assert.Equal(0, host.Initializes);

        window.Close();
        Assert.Equal(1, host.CloseRequests);
    }

    [Fact]
    public void Run_needs_a_host_with_a_loop_and_a_callback_host_starts_instead()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.Throws<InvalidOperationException>(window.Run);
        Assert.False(window.IsRunning);
    }

    [Fact]
    public void Stopping_what_never_started_is_done_at_once_and_always_the_same_task()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);

        Task stop = window.StopAsync();
        Assert.True(stop.IsCompletedSuccessfully);
        Assert.Same(stop, window.StopAsync());
    }

    [Fact]
    public void What_the_host_says_about_the_display_is_what_the_engine_sees()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);

        // A Retina screen, twice as many pixels as points, at 200%
        host.Events!.DisplayChanged(new PlatformDisplay(new Vector2(2560, 1600), new Vector2(2560, 1600), new Vector2(1280, 800), new Vector2(3024, 1964), new Vector2(2, 2), 2.0f), resized: true);

        Assert.Equal(new Vector2(2560, 1600), window.ViewportSize);
        Assert.Equal(new Vector2(1280, 800), window.PointSize);
        Assert.Equal(new Vector2(3024, 1964), window.ScreenSize);
        Assert.Equal(new Vector2(2, 2), window.PixelScale);
        Assert.Equal(2.0f, window.DisplayScale);
        Assert.Equal(1.0f, window.WindowUnitScale);
        Assert.Equal(new Vector2(20, 10), window.PointerToPixels(new Vector2(10, 5)));
    }

    [Fact]
    public void A_game_never_lets_its_thread_wait_for_events_and_ignores_focus()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.False(host.Events!.MayWaitForEvents);
        host.Events.FocusChanged(false);
        Assert.True(window.IsFocused);

        // Minimised it does hear, a fight pauses on it
        host.Events.MinimizedChanged(true);
        Assert.True(window.IsMinimized);
        host.Events.MinimizedChanged(false);
        Assert.False(window.IsMinimized);
    }

    [Fact]
    public void An_application_waits_for_events_until_input_comes_and_hears_focus()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config with { Mode = RunMode.Application }, host);

        Assert.True(host.Events!.MayWaitForEvents);
        host.Events.InputHeard();
        Assert.False(host.Events.MayWaitForEvents);

        host.Events.FocusChanged(false);
        Assert.False(window.IsFocused);
        host.Events.FocusChanged(true);
        Assert.True(window.IsFocused);
    }

    [Fact]
    public void Asked_to_close_the_engine_asks_whoever_wants_a_say()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.True(host.Events!.MayClose());
        window.CloseRequested = () => false;
        Assert.False(host.Events.MayClose());
        window.CloseRequested = () => throw new InvalidOperationException();
        Assert.True(host.Events.MayClose());
    }

    [Fact]
    public void Events_processed_reach_whoever_listens_on_the_platform_thread()
    {
        var host = new FakeHost();
        using var window = new WindowManager(Config, host);
        int heard = 0;
        window.EventsProcessed += () => heard++;

        host.Events!.EventsProcessed();
        Assert.Equal(1, heard);
    }

    [Fact]
    public void A_device_made_for_a_host_asks_it_for_speakers_only_when_it_is_to_open_them()
    {
        // A scripted run with the sound off asks nobody, which is what HORIZON_AUDIO=off is for
        if (string.Equals(Environment.GetEnvironmentVariable("HORIZON_AUDIO"), "off", StringComparison.OrdinalIgnoreCase))
            return;

        var host = new FakeHost();

        using (var none = AudioDevice.For(open: false, host))
            Assert.False(none.IsOpen);
        Assert.Equal(0, host.OutputsMade);

        var device = AudioDevice.For(open: true, host);
        Assert.Equal(1, host.OutputsMade);
        Assert.True(device.IsOpen);

        // The device owns what it was handed, the host doesn't dispose it as well
        device.Dispose();
        Assert.Equal(1, host.Output!.Disposals);
        host.Dispose();
        Assert.Equal(1, host.Output.Disposals);
    }

    [Fact]
    public void The_input_manager_listens_to_the_platform_and_starts_its_gamepads_after_it()
    {
        var host = new FakeHost();
        var holder = new Holder();
        var window = holder.AddComponent(new WindowManager(Config, host));
        var input = holder.AddComponent(new InputManager());
        try
        {
            input.Initialize();

            Assert.Equal(new Vector2(40, 30), input.Mouse.LivePosition);
            Assert.True(host.Input.Listened);
            Assert.NotNull(host.Input.Gamepads);
            Assert.Equal(1, host.Input.Gamepads!.Starts);
            Assert.True(host.Input.Gamepads.Listened);

            // The clipboard is the platform's
            host.Input.ClipboardText = "pasted";
            Assert.Equal("pasted", input.ClipboardText);
            input.ClipboardText = "copied";
            Assert.Equal("copied", host.Input.ClipboardText);

            // Captured on the platform's thread, which is where the host runs what's posted
            input.Mouse.Captured = true;
            Assert.Empty(host.Input.Captures);
            host.RunPosted();
            Assert.Equal([true], host.Input.Captures);

            // What the platform hears goes through the queue to the next update
            host.Input.Raise(InputEvent.KeyChange(Key.A, true));
            Assert.False(input.Keyboard.IsDown(Key.A));
            input.Enabled = true;
            input.UpdateState(0.0f);
            Assert.True(input.Keyboard.IsDown(Key.A));
        }
        finally
        {
            input.Dispose();
            window.Dispose();
        }

        // Let go of by the manager, the host disposes what it made
        Assert.False(host.Input.Listened);
        Assert.Equal(1, host.Input.Gamepads!.Disposals);
    }

    [Fact]
    public void Without_gamepads_the_platform_is_never_asked_for_them()
    {
        var host = new FakeHost();
        var holder = new Holder();
        var window = holder.AddComponent(new WindowManager(Config, host));
        var input = holder.AddComponent(new InputManager { ReadsGamepads = false });
        input.Initialize();

        Assert.Null(host.Input.Gamepads);
        input.Dispose();
        window.Dispose();
    }

    private sealed class Holder : Entity;

    /// <summary>A host with no window and no loop, which runs what's posted when it's told to.</summary>
    internal sealed class FakeHost : IPlatformHost
    {
        private readonly PlatformDispatcher dispatcher = new(() => { });

        public int Attaches, Initializes, CloseRequests, Disposals, OutputsMade;
        public IPlatformEvents? Events { get; private set; }
        public FakeInput Input { get; } = new();
        public FakeOutput? Output { get; private set; }

        public PlatformFeatures Features => PlatformFeatures.None;
        public IPlatformWindow? Window => null;
        public IPlatformSurfaceAhead? SurfaceAhead => null;
        public IPlatformSurface Surface => throw new InvalidOperationException("No surface in a test.");
        IPlatformInput IPlatformHost.Input => Input;
        public PlatformState State { get; } = new(PlatformDisplay.Of(new Vector2(800, 600)), Focused: true, Minimized: false);

        public IAudioOutput? CreateAudioOutput()
        {
            OutputsMade++;
            return Output = new FakeOutput();
        }

        public void Attach(IPlatformEvents events)
        {
            Attaches++;
            Events = events;
        }

        public void Initialize()
        {
            Initializes++;
            dispatcher.BindToCurrentThread();
        }

        public bool IsPlatformThread => dispatcher.IsPlatformThread;
        public void Post(Action work) => dispatcher.Post(work);
        public void Invoke(Action work) => dispatcher.Invoke(work);
        public void Wake() { }
        public void RequestClose() => CloseRequests++;
        public void RunPosted() => dispatcher.Drain();
        public void Dispose() => Disposals++;
    }

    internal sealed class FakeInput : IPlatformInput
    {
        private Action<InputEvent>? listeners;
        public List<bool> Captures { get; } = [];
        public FakeGamepads? Gamepads { get; private set; }
        public bool Listened => listeners is not null;

        public event Action<InputEvent>? EventReceived
        {
            add => listeners += value;
            remove => listeners -= value;
        }

        public Vector2 PointerPosition => new(40, 30);
        public void SetPointerCaptured(bool captured) => Captures.Add(captured);
        public string ClipboardText { get; set; } = string.Empty;
        public IPlatformGamepads? OpenGamepads() => Gamepads = new FakeGamepads();
        public void Raise(InputEvent input) => listeners?.Invoke(input);
    }

    internal sealed class FakeGamepads : IPlatformGamepads
    {
        private Action<InputEvent>? listeners;
        public int Starts, Disposals;
        public bool Listened => listeners is not null;

        public event Action<InputEvent>? EventReceived
        {
            add => listeners += value;
            remove => listeners -= value;
        }

        public void Start() => Starts++;
        public void Dispose() => Disposals++;
    }

    internal sealed class FakeOutput : IAudioOutput
    {
        public int Disposals;
        public AudioFormat Open(AudioFormat wanted) => wanted;
        public void Start(AudioRender render) { }
        public void Pause() { }
        public void Resume() { }
        public void Dispose() => Disposals++;
    }
}
