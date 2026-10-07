namespace Horizon.Testing;

/// <summary>
/// One line of the list of keys the <see cref="TestHost"/> shows while a test runs.
/// </summary>
/// <param name="Input">The key or button as it should read on screen, e.g. "G" or "Hold right click".</param>
/// <param name="Action">What it does, e.g. "toggle gravity".</param>
public sealed record TestControl(string Input, string Action);

/// <summary>
/// For a test scene you can poke at. The host lists whatever it returns on screen, so the scene never has to
/// print its own keys anywhere (and they can't go out of date with the code, which they bloody always do).
/// A scene that only wants to be looked at just doesn't implement this.
/// <code>
/// public IReadOnlyList&lt;TestControl&gt; Controls { get; } = [new("G", "toggle gravity")];
/// </code>
/// </summary>
public interface ITestControls
{
    /// <summary>The keys and buttons the scene reacts to, in the order they are listed.</summary>
    IReadOnlyList<TestControl> Controls { get; }
}
