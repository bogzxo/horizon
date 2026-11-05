namespace Horizon.Graphics;

/// <summary>Where the device stands with the surface it draws into, see <see cref="GraphicsDevice.SurfaceState"/>.</summary>
public enum SurfaceState
{
    /// <summary>There is a surface, and frames are shown in it whenever it isn't nothing by nothing.</summary>
    Attached,

    /// <summary>
    /// The driver said the surface is gone (VK_ERROR_SURFACE_LOST_KHR). The swapchain and the surface are let go of,
    /// and the device makes them again from the same platform surface every so often until that works.
    /// </summary>
    Lost,

    /// <summary>The host took the surface away (<see cref="GraphicsDevice.DetachSurface"/>). Frames go on, with nothing shown.</summary>
    Detached
}

/// <summary>How a <see cref="GraphicsDevice.AttachSurface"/> went.</summary>
public enum SurfaceAttachResult
{
    /// <summary>There's a surface now, and a swapchain the moment it has a size.</summary>
    Attached,

    /// <summary>There was one already. Detach it first.</summary>
    AlreadyAttached,

    /// <summary>The platform has no native window right now.</summary>
    Unavailable,

    /// <summary>The surface wants instance extensions the instance wasn't made with.</summary>
    MissingExtensions,

    /// <summary>The queue the device draws with can't show pictures in this surface.</summary>
    CannotPresent
}
