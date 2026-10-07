namespace Horizon.Testing;

/// <summary>
/// One line of the list of keys the <see cref="TestHost"/> shows while a test runs.
/// </summary>
/// <param name="Input">The key or button as it should read on screen, e.g. "G" or "Hold right click".</param>
/// <param name="Action">What it does, e.g. "toggle gravity".</param>
public sealed record TestControl(string Input, string Action);

/// <summary>
/// For a test scene that can be interacted with. The host lists whatever it returns on screen, so the
/// scene has no need to print its keys anywhere. A scene without any just doesn't implement this.
/// </summary>
public interface ITestControls
{
    /// <summary>The keys and buttons the scene reacts to, in the order they are listed.</summary>
    IReadOnlyList<TestControl> Controls { get; }
}
