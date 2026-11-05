
using System.Runtime.InteropServices;

using Horizon.Graphics;
using Horizon.Graphics.Vulkan;
using Horizon.Platform;
using Horizon.Platform.Desktop;

using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Horizon.Tests.Platform;

public class SurfaceTests
{
    [Theory]
    [InlineData(Result.Success, false, (int)SwapchainStatus.Ready)]
    [InlineData(Result.SuboptimalKhr, false, (int)SwapchainStatus.Suboptimal)]
    [InlineData(Result.SuboptimalKhr, true, (int)SwapchainStatus.Ready)]
    [InlineData(Result.ErrorOutOfDateKhr, false, (int)SwapchainStatus.OutOfDate)]
    [InlineData(Result.ErrorSurfaceLostKhr, false, (int)SwapchainStatus.SurfaceLost)]
    [InlineData(Result.NotReady, false, (int)SwapchainStatus.NotReady)]
    [InlineData(Result.Timeout, false, (int)SwapchainStatus.NotReady)]
    public void Swapchain_results_are_told_apart(Result result, bool sizedByHand, int expected) =>
        Assert.Equal((SwapchainStatus)expected, Swapchain.Classify(result, sizedByHand));

    [Theory]
    [InlineData(Result.ErrorDeviceLost)]
    [InlineData(Result.ErrorOutOfDeviceMemory)]
    [InlineData(Result.ErrorInitializationFailed)]
    public void Results_that_are_not_about_the_surface_are_left_to_throw(Result result) =>
        Assert.Null(Swapchain.Classify(result, false));

    [Fact]
    public void Identity_is_only_asked_for_where_the_surface_can_do_it()
    {
        var rotated = SurfaceTransformFlagsKHR.Rotate90BitKhr;
        var both = SurfaceTransformFlagsKHR.IdentityBitKhr | rotated;

        Assert.Equal(rotated, Swapchain.PickTransform(SurfaceTransformPolicy.Current, both, rotated));
        Assert.Equal(SurfaceTransformFlagsKHR.IdentityBitKhr, Swapchain.PickTransform(SurfaceTransformPolicy.Identity, both, rotated));
        Assert.Equal(rotated, Swapchain.PickTransform(SurfaceTransformPolicy.Identity, rotated, rotated));
    }

    [Fact]
    public void Composition_uses_a_supported_mode_after_surface_recreation()
    {
        var opaque = CompositeAlphaFlagsKHR.OpaqueBitKhr;
        var inherit = CompositeAlphaFlagsKHR.InheritBitKhr;
        Assert.Equal(opaque, Swapchain.PickCompositeAlpha(opaque | inherit));
        Assert.Equal(inherit, Swapchain.PickCompositeAlpha(inherit));
        Assert.Equal(CompositeAlphaFlagsKHR.PreMultipliedBitKhr, Swapchain.PickCompositeAlpha(CompositeAlphaFlagsKHR.PreMultipliedBitKhr));
        Assert.Equal(CompositeAlphaFlagsKHR.PostMultipliedBitKhr, Swapchain.PickCompositeAlpha(CompositeAlphaFlagsKHR.PostMultipliedBitKhr));
        Assert.Throws<InvalidOperationException>(() => Swapchain.PickCompositeAlpha(0));
    }

    [Fact]
    public void Missing_extensions_are_the_wanted_ones_the_instance_has_not_got()
    {
        string[] have = ["VK_KHR_surface", "VK_KHR_win32_surface", "VK_EXT_debug_utils"];

        Assert.Empty(SurfaceLifecycle.MissingExtensions(["VK_KHR_win32_surface", "VK_KHR_surface"], have));
        Assert.Equal(["VK_KHR_xlib_surface"], SurfaceLifecycle.MissingExtensions(["VK_KHR_surface", "VK_KHR_xlib_surface"], have));
        Assert.Equal(["vk_khr_surface"], SurfaceLifecycle.MissingExtensions(["vk_khr_surface"], have));
        Assert.Equal(["VK_KHR_surface"], SurfaceLifecycle.MissingExtensions(["VK_KHR_surface"], []));
    }

    [Fact]
    public void The_surface_only_changes_between_frames()
    {
        var life = new SurfaceLifecycle();
        life.RequireBetweenFrames("taken away");

        life.EnterFrame();
        Assert.Throws<InvalidOperationException>(() => life.RequireBetweenFrames("taken away"));
        Assert.Throws<InvalidOperationException>(life.EnterFrame);

        life.LeaveFrame();
        life.RequireBetweenFrames("given");
        life.EnterFrame();
    }

    [Fact]
    public void No_picture_is_taken_without_a_surface_and_a_swapchain()
    {
        var life = new SurfaceLifecycle();
        Assert.True(life.MayAcquire(swapchainLive: true));
        Assert.False(life.MayAcquire(swapchainLive: false));

        life.Detached();
        Assert.False(life.MayAcquire(swapchainLive: true));

        life.Attached();
        life.Lost();
        Assert.False(life.MayAcquire(swapchainLive: true));
    }

    [Fact]
    public void A_lost_surface_is_tried_again_every_so_often()
    {
        var life = new SurfaceLifecycle();
        Assert.False(life.ShouldRecover());

        life.Lost();
        Assert.Equal(SurfaceState.Lost, life.State);
        Assert.Equal(1, life.Losses);
        Assert.True(life.ShouldRecover());

        life.RecoverFailed();
        for (int i = 0; i < SurfaceLifecycle.RECOVER_EVERY; i++)
            Assert.False(life.ShouldRecover());
        Assert.True(life.ShouldRecover());

        life.Attached();
        Assert.Equal(SurfaceState.Attached, life.State);
        Assert.False(life.ShouldRecover());
    }

    [Fact]
    public void A_surface_taken_away_is_not_lost_as_well()
    {
        var life = new SurfaceLifecycle();
        life.Detached();
        life.Lost();

        Assert.Equal(SurfaceState.Detached, life.State);
        Assert.Equal(0, life.Losses);
        Assert.False(life.ShouldRecover());

        // A loss on top of a loss is still the one
        life.Attached();
        life.Lost();
        life.Lost();
        Assert.Equal(1, life.Losses);
    }

    [Fact]
    public void Surface_size_is_empty_when_either_side_is_nothing()
    {
        Assert.True(new SurfaceSize(0, 720).IsEmpty);
        Assert.True(new SurfaceSize(1280, 0).IsEmpty);
        Assert.False(new SurfaceSize(1, 1).IsEmpty);
    }

    [Fact]
    public void The_desktop_surface_hands_over_the_window_extensions_and_its_surface()
    {
        using var window = new FakeVkSurface(["VK_KHR_surface", "VK_KHR_win32_surface"], made: 0x1234);
        var surface = new SilkSurface(window, null, () => new SurfaceSize(800, 600), () => true);

        Assert.Equal(["VK_KHR_surface", "VK_KHR_win32_surface"], surface.VulkanInstanceExtensions);
        Assert.True(surface.IsAvailable);
        Assert.Equal(new SurfaceSize(800, 600), surface.PixelSize);
        Assert.Equal(SurfaceTransformPolicy.Current, ((IPlatformSurface)surface).TransformPolicy);

        Assert.Equal(0x1234UL, surface.CreateVulkanSurface(0x99, _ => 0));
        Assert.Equal((nint)0x99, window.Instance);
        Assert.Equal(1, window.Made);
    }

    [Fact]
    public void The_desktop_surface_can_be_made_on_the_thread_of_the_window()
    {
        using var window = new FakeVkSurface(["VK_KHR_surface"], made: 7);
        int handedOver = 0;
        var surface = new SilkSurface(window, work => { handedOver++; work(); }, () => default, () => false);

        Assert.False(surface.IsAvailable);
        Assert.True(surface.PixelSize.IsEmpty);
        Assert.Equal(7UL, surface.CreateVulkanSurface(1, _ => 0));
        Assert.Equal(1, handedOver);
    }

    [Fact]
    public void Ahead_of_the_window_is_only_desktop_windows_and_not_sdl()
    {
        Assert.Null(Win32SurfaceAhead.ForThisMachine(sdl: true));
        if (!OperatingSystem.IsWindows())
            Assert.Null(Win32SurfaceAhead.ForThisMachine(sdl: false));
    }

    [Fact]
    public unsafe void Ahead_of_the_window_presentation_is_asked_through_the_instance_function()
    {
        if (Win32SurfaceAhead.ForThisMachine(sdl: false) is not { } ahead)
            return;

        Assert.Equal(["VK_KHR_surface", "VK_KHR_win32_surface"], ahead.VulkanInstanceExtensions);
        Assert.False(ahead.PresentsWithoutSurface(1, 2, 0, _ => 0));

        string? asked = null;
        nint function = (nint)(delegate* unmanaged<nint, uint, uint>)&FakePresentationSupport;
        Assert.True(ahead.PresentsWithoutSurface(1, 0x55, 3, name => { asked = name; return function; }));
        Assert.Equal("vkGetPhysicalDeviceWin32PresentationSupportKHR", asked);
        Assert.False(ahead.PresentsWithoutSurface(1, 0x55, 4, _ => function));
    }

    // Says yes for queue family 3 of the card at 0x55 and no to everything else
    [UnmanagedCallersOnly]
    private static uint FakePresentationSupport(nint physicalDevice, uint family) => physicalDevice == 0x55 && family == 3 ? 1u : 0u;

    /// <summary>A window's Vulkan surface maker that remembers what it was asked, with its extensions as C strings.</summary>
    private sealed unsafe class FakeVkSurface : IVkSurface, IDisposable
    {
        private readonly nint names;
        private readonly int count;
        private readonly ulong made;

        public nint Instance { get; private set; }
        public int Made { get; private set; }

        public FakeVkSurface(string[] extensions, ulong made)
        {
            this.made = made;
            count = extensions.Length;
            names = SilkMarshal.StringArrayToPtr(extensions);
        }

        public VkNonDispatchableHandle Create<T>(VkHandle instance, T* allocator) where T : unmanaged
        {
            Instance = instance.Handle;
            Made++;
            return new VkNonDispatchableHandle(made);
        }

        public byte** GetRequiredExtensions(out uint extensionCount)
        {
            extensionCount = (uint)count;
            return (byte**)names;
        }

        public void Dispose() => SilkMarshal.Free(names);
    }
}
