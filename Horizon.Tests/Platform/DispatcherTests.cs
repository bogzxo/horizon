using Horizon.Core;
using Horizon.Engine;
using Horizon.Platform;
using Horizon.Platform.Desktop;

namespace Horizon.Tests.Platform;

/// <summary>
/// The platform's thread is the one that bound the dispatcher, not just any that isn't the one that draws, and nothing
/// waiting on it is left to hang once the platform stops.
/// </summary>
public class DispatcherTests
{
    [Fact]
    public void Before_a_thread_is_the_platforms_work_is_done_right_away()
    {
        var dispatcher = new PlatformDispatcher(() => { });
        int done = 0;
        dispatcher.Invoke(() => done++);
        Assert.Equal(1, done);
        Assert.False(dispatcher.IsBound);
    }

    [Fact]
    public void Invoke_on_the_platform_thread_is_done_there_and_from_another_waits_for_it()
    {
        var woken = new ManualResetEventSlim(false);
        var dispatcher = new PlatformDispatcher(woken.Set);
        dispatcher.BindToCurrentThread();
        Assert.True(dispatcher.IsPlatformThread);

        int ranOn = 0;
        dispatcher.Invoke(() => ranOn = Environment.CurrentManagedThreadId);
        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);

        // Another thread that isn't the one that draws is still not the platform's, so it waits its turn
        Task other = Task.Run(() =>
        {
            Assert.False(dispatcher.IsPlatformThread);
            dispatcher.Invoke(() => ranOn = Environment.CurrentManagedThreadId);
        });
        Assert.True(woken.Wait(5000));
        Assert.False(other.Wait(100));
        while (!other.IsCompleted)
            dispatcher.Drain();
        other.GetAwaiter().GetResult();
        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);
    }

    [Fact]
    public void What_the_work_threw_is_thrown_to_whoever_waited()
    {
        var dispatcher = new PlatformDispatcher(() => { });
        dispatcher.BindToCurrentThread();
        Task other = Task.Run(() => dispatcher.Invoke(() => throw new InvalidOperationException("nope")));
        while (!other.IsCompleted)
            dispatcher.Drain();
        var thrown = Assert.Throws<InvalidOperationException>(() => other.GetAwaiter().GetResult());
        Assert.Equal("nope", thrown.Message);
    }

    [Fact]
    public void Closed_it_lets_go_of_whoever_waits_and_takes_nothing_new()
    {
        var dispatcher = new PlatformDispatcher(() => { });
        dispatcher.BindToCurrentThread();
        var waiting = new ManualResetEventSlim(false);
        Task other = Task.Run(() =>
        {
            waiting.Set();
            dispatcher.Invoke(() => { });
        });
        Assert.True(waiting.Wait(5000));
        Thread.Sleep(50);
        dispatcher.Close();

        Assert.Throws<ObjectDisposedException>(() => other.GetAwaiter().GetResult());

        int posted = 0;
        dispatcher.Post(() => posted++);
        Assert.Equal(0, dispatcher.Drain());
        Assert.Equal(0, posted);
    }

    [Fact]
    public void An_engine_needs_a_platform_and_a_handed_one_wins()
    {
        IPlatform? installed = PlatformInstallation.Current;
        try
        {
            PlatformInstallation.Uninstall();
            Assert.Throws<InvalidOperationException>(() => PlatformInstallation.Resolve(null));

            var handed = new FakePlatform();
            Assert.Same(handed, PlatformInstallation.Resolve(handed));

            DesktopPlatform.Install();
            Assert.Same(DesktopPlatform.Instance, PlatformInstallation.Current);
            Assert.Same(handed, PlatformInstallation.Resolve(handed));
            Assert.Same(DesktopPlatform.Instance, PlatformInstallation.Resolve(new GameEngineConfiguration().Platform));
        }
        finally
        {
            if (installed is null) PlatformInstallation.Uninstall();
            else PlatformInstallation.Install(installed);
        }
    }

    [Fact]
    public void The_desktop_makes_a_fresh_host_for_every_engine_and_says_what_it_has()
    {
        var request = new PlatformHostRequest(WindowManagerConfiguration.Default1600x900, ApplicationBehaviour.None, ReadsGamepads: false);
        using IPlatformHost first = DesktopPlatform.Instance.CreateHost(request);
        using IPlatformHost second = DesktopPlatform.Instance.CreateHost(request);
        Assert.NotSame(first, second);

        Assert.True((first.Features & PlatformFeatures.Window) != 0);
        Assert.True((first.Features & PlatformFeatures.Clipboard) != 0);
        Assert.False((first.Features & PlatformFeatures.Gamepads) != 0);
        Assert.False((first.Features & PlatformFeatures.WindowFrame) != 0);

        using IPlatformHost framed = DesktopPlatform.Instance.CreateHost(request with { Window = request.Window with { CustomFrame = true }, ReadsGamepads = true });
        Assert.True((framed.Features & PlatformFeatures.WindowFrame) != 0);
        Assert.True((framed.Features & PlatformFeatures.Gamepads) != 0);
        Assert.Equal(DesktopWindow.UsesSdl, (framed.Features & PlatformFeatures.DragArea) != 0);

        // Nothing native before the engine attaches, and nothing published before the window loads
        Assert.Throws<InvalidOperationException>(() => first.Surface);
        Assert.Throws<InvalidOperationException>(() => first.Input);
        Assert.Null(first.Window);
    }

    private sealed class FakePlatform : IPlatform
    {
        public IPlatformHost CreateHost(PlatformHostRequest request) => new HostTests.FakeHost();
        public IAudioOutput? CreateAudioOutput() => null;
    }
}
