namespace Horizon.Platform;

/// <summary>
/// Reports the host machine's power source and energy-saving state so the engine can pace idle and calm work more
/// aggressively on battery or in Low Power Mode / Battery Saver without slowing down active input or animations.
/// </summary>
public interface IPlatformPower
{
    /// <summary>Whether the device is currently running on battery power rather than AC.</summary>
    bool IsOnBattery { get; }

    /// <summary>Whether the OS energy-saving mode (macOS Low Power Mode, Windows Battery/Energy Saver, Linux low-power profile) is active.</summary>
    bool IsLowPowerMode { get; }

    /// <summary>Raised when <see cref="IsOnBattery"/> or <see cref="IsLowPowerMode"/> changes. May fire on any thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Global access point for the active <see cref="IPlatformPower"/> implementation. Defaults to a no-op AC provider
/// until a platform package (such as <c>Horizon.Platform.Desktop</c>) registers its native implementation.
/// </summary>
public static class PlatformPower
{
    private static readonly Lock Gate = new();
    private static volatile IPlatformPower current = NoopPower.Instance;

    /// <summary>The active platform power provider.</summary>
    public static IPlatformPower Current => current;

    /// <summary>Whether the device is currently running on battery power.</summary>
    public static bool IsOnBattery => current.IsOnBattery;

    /// <summary>Whether the OS Low Power Mode / Battery Saver is currently active.</summary>
    public static bool IsLowPowerMode => current.IsLowPowerMode;

    /// <summary>Whether the engine should conserve energy (either Low Power Mode is active or the machine is on battery).</summary>
    public static bool SavesEnergy
    {
        get
        {
            IPlatformPower provider = current;
            return provider.IsLowPowerMode || provider.IsOnBattery;
        }
    }

    /// <summary>Raised when the active provider reports a power-state transition.</summary>
    public static event Action? Changed;

    /// <summary>Replaces the active power provider and forwards its <see cref="IPlatformPower.Changed"/> notifications.</summary>
    public static void Register(IPlatformPower provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Swap(provider);
    }

    /// <summary>Temporarily replaces <see cref="Current"/> for unit tests and restores the previous provider on dispose.</summary>
    internal static IDisposable UseForTesting(IPlatformPower? overrideProvider)
    {
        IPlatformPower previous = Swap(overrideProvider ?? NoopPower.Instance);
        return new TestingScope(previous);
    }

    private static IPlatformPower Swap(IPlatformPower next)
    {
        lock (Gate)
        {
            IPlatformPower previous = current;
            if (ReferenceEquals(previous, next))
                return previous;

            previous.Changed -= OnProviderChanged;
            current = next;
            next.Changed += OnProviderChanged;
            return previous;
        }
    }

    private static void OnProviderChanged() => Changed?.Invoke();

    private sealed class TestingScope(IPlatformPower previous) : IDisposable
    {
        private IPlatformPower? restore = previous;

        public void Dispose()
        {
            IPlatformPower? target = Interlocked.Exchange(ref restore, null);
            if (target is not null)
                Swap(target);
        }
    }

    private sealed class NoopPower : IPlatformPower
    {
        public static readonly NoopPower Instance = new();
        public bool IsOnBattery => false;
        public bool IsLowPowerMode => false;
        public event Action? Changed
        {
            add { }
            remove { }
        }
    }
}
