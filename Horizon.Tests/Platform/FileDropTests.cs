using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Core;
using Horizon.Platform.Desktop;

namespace Horizon.Tests.Platform;

/// <summary>
/// Files dragged onto the window from outside the program, as the host says it and as the program takes it: what is held
/// over the window while it is dragged, and the drops kept until they are taken.
/// </summary>
public class FileDropTests
{
    private static readonly WindowManagerConfiguration Config = WindowManagerConfiguration.Default1600x900 with { Mode = RunMode.Application };

    [Fact]
    public void A_drop_the_host_says_is_kept_until_it_is_taken_once()
    {
        var host = new HostTests.FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.False(window.TryTakeFileDrop(out _));
        host.Events!.FilesDragged(new FileDrop(FileDropKind.Drop, ["C:/a.txt", "C:/folder"], new Vector2(40, 30)));

        Assert.True(window.TryTakeFileDrop(out FileDrop drop));
        Assert.Equal(["C:/a.txt", "C:/folder"], drop.Paths);
        Assert.Equal(new Vector2(40, 30), drop.Position);
        Assert.False(window.TryTakeFileDrop(out _));
    }

    [Fact]
    public void Drops_are_taken_in_the_order_they_came_and_empty_ones_are_not_kept()
    {
        var host = new HostTests.FakeHost();
        using var window = new WindowManager(Config, host);

        host.Events!.FilesDragged(new FileDrop(FileDropKind.Drop, ["first"], Vector2.Zero));
        host.Events.FilesDragged(new FileDrop(FileDropKind.Drop, [], Vector2.Zero));
        host.Events.FilesDragged(new FileDrop(FileDropKind.Drop, ["second"], Vector2.Zero));

        Assert.True(window.TryTakeFileDrop(out FileDrop first));
        Assert.True(window.TryTakeFileDrop(out FileDrop second));
        Assert.Equal("first", first.Paths[0]);
        Assert.Equal("second", second.Paths[0]);
        Assert.False(window.TryTakeFileDrop(out _));
    }

    [Fact]
    public void What_is_held_over_the_window_follows_the_pointer_and_goes_when_it_leaves_or_drops()
    {
        var host = new HostTests.FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.Null(window.FileDrag);
        host.Events!.FilesDragged(new FileDrop(FileDropKind.Enter, ["one", "two"], new Vector2(1, 2)));
        Assert.Equal(FileDropKind.Enter, window.FileDrag!.Kind);
        Assert.Equal(2, window.FileDrag.Paths.Count);

        // Moving about says only where, the files are the ones it came in with
        host.Events.FilesDragged(new FileDrop(FileDropKind.Over, [], new Vector2(50, 60)));
        Assert.Equal(FileDropKind.Over, window.FileDrag!.Kind);
        Assert.Equal(new Vector2(50, 60), window.FileDrag.Position);
        Assert.Equal(["one", "two"], window.FileDrag.Paths);

        host.Events.FilesDragged(FileDrop.Left);
        Assert.Null(window.FileDrag);

        host.Events.FilesDragged(new FileDrop(FileDropKind.Enter, ["one"], Vector2.Zero));
        host.Events.FilesDragged(new FileDrop(FileDropKind.Drop, ["one"], new Vector2(5, 5)));
        Assert.Null(window.FileDrag);
        Assert.True(window.TryTakeFileDrop(out _));
    }

    [Fact]
    public void A_drop_wakes_an_application_that_went_quiet()
    {
        var host = new HostTests.FakeHost();
        using var window = new WindowManager(Config, host);

        Assert.True(host.Events!.MayWaitForEvents);
        host.Events.FilesDragged(new FileDrop(FileDropKind.Drop, ["a"], Vector2.Zero));
        Assert.False(host.Events.MayWaitForEvents);
    }

    [Fact]
    public void A_drop_posted_from_another_thread_is_taken_on_this_one()
    {
        var host = new HostTests.FakeHost();
        using var window = new WindowManager(Config, host);

        Task.Run(() => window.PostFileDrop(new FileDrop(FileDropKind.Drop, ["elsewhere"], Vector2.Zero))).Wait();
        Assert.True(window.TryTakeFileDrop(out FileDrop drop));
        Assert.Equal("elsewhere", drop.Paths[0]);
    }

    [Fact]
    public void A_platform_that_never_drags_files_has_nothing_to_say_about_them()
    {
        IPlatformEventsWithoutDrops events = new();
        ((Horizon.Platform.IPlatformEvents)events).FilesDragged(new FileDrop(FileDropKind.Drop, ["a"], Vector2.Zero));
    }

    /// <summary>A host's events as one written before files could be dropped, which still builds and ignores them.</summary>
    private sealed class IPlatformEventsWithoutDrops : Horizon.Platform.IPlatformEvents
    {
        public void DisplayChanged(Horizon.Platform.PlatformDisplay display, bool resized) { }
        public void FocusChanged(bool focused) { }
        public void MinimizedChanged(bool minimized) { }
        public void InputHeard() { }
        public void EventsProcessed() { }
        public void RequestFrames() { }
        public void Wake() { }
        public bool MayClose() => true;
        public bool MayWaitForEvents => true;
    }

    [Fact]
    public unsafe void Windows_reads_the_files_and_folders_out_of_what_explorer_drags()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string folder = Path.Combine(Path.GetTempPath(), "horizon drop " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "dropped file.txt");
        File.WriteAllText(file, "dropped");
        try
        {
            foreach (string path in new[] { file, folder })
            {
                nint data = DataObjectOf(path);
                Assert.NotEqual(0, data);
                try
                {
                    Assert.Equal([path], WindowsDropTarget.PathsIn(data));
                }
                finally
                {
                    Marshal.Release(data);
                }
            }

            Assert.Empty(WindowsDropTarget.PathsIn(0));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_window_made_on_one_thread_is_a_drop_target_from_the_targets_own()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The window's thread is in the multithreaded apartment, as the program's main thread is, which OLE won't take
        // a target from. The target registers it from a thread of its own
        nint window = CreateWindowExW(0, "STATIC", "drop target", 0, 0, 0, 100, 100, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            using WindowsDropTarget? target = WindowsDropTarget.Start(window, _ => { });
            Assert.NotNull(target);
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extended, string kind, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    /// <summary>The data object Explorer hands a drop target for a file or a folder, asked of the shell's item for it.</summary>
    private static unsafe nint DataObjectOf(string path)
    {
        Guid shellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        Guid dataObjectHandler = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
        Guid dataObject = new("0000010e-0000-0000-c000-000000000046");
        nint item = 0;
        Assert.True(SHCreateItemFromParsingName(path, 0, &shellItem, &item) >= 0);
        try
        {
            nint made = 0;
            var bind = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(nint**)item)[3];
            return bind(item, 0, &dataObjectHandler, &dataObject, &made) >= 0 ? made : 0;
        }
        finally
        {
            Marshal.Release(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern unsafe int SHCreateItemFromParsingName(string path, nint context, Guid* iid, nint* item);
}
