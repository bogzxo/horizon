namespace Horizon.Platform;

/// <summary>
/// How a program asks for the person's eye while another one has the screen: a Mac's Dock icon bounces and carries a badge.
/// Where there is none (everywhere but a Mac) <see cref="PlatformAttention.Current"/> is null. Every call is from any thread
/// and does nothing while the program is the active one.
/// </summary>
public interface IPlatformAttention
{
    /// <summary>Has the icon bounce once (<paramref name="critical"/> false) or until the program is looked at (true), only when the program isn't active.</summary>
    void RequestAttention(bool critical);

    /// <summary>Puts a short text on the icon, null or empty takes it off (a count of what waits).</summary>
    void SetBadge(string? label);
}

/// <summary>Where a platform's <see cref="IPlatformAttention"/> is found, set by the platform's own assembly as it loads.</summary>
public static class PlatformAttention
{
    /// <summary>The platform's way to ask for attention, null where it has none.</summary>
    public static IPlatformAttention? Current { get; set; }
}
