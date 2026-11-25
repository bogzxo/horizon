namespace Horizon.Platform;

/// <summary>
/// What the system says about how things should look: light or dark, and the accent colour the person picked. A Mac has
/// it (System Settings, Appearance), where there is none the host's <see cref="IPlatformHost.Appearance"/> is null.
/// </summary>
public interface IPlatformAppearance
{
    /// <summary>Whether the system is in its dark appearance. Any thread, as of the last time it was read.</summary>
    bool IsDark { get; }

    /// <summary>The system's accent colour in sRGB, each 0..1, or null when it has none. Any thread, as of the last time it was read.</summary>
    (float R, float G, float B)? Accent { get; }

    /// <summary>
    /// Raised when the appearance or the accent changed, on the platform's thread. A listener that owns state of another
    /// thread (a UI) posts to it and reads <see cref="IsDark"/> and <see cref="Accent"/> there.
    /// </summary>
    event Action Changed;
}
