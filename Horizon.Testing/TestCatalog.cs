using Horizon.Testing.Scenes;

namespace Horizon.Testing;

/// <summary>
/// Every test the selector offers, in the order it lists them.
/// To add a test, write a scene for it and give it a line here; nothing else needs to know about it.
/// </summary>
internal static class TestCatalog
{
    public static readonly TestDefinition[] Tests =
    [
        new("ui", "User interface", "Layout, skinning, pointer input and HIDL scripting.", () => new UITestScene()),
        new("ui-selftest", "UI self-test", "The UI test clicking through itself and checking the results.", () => new UITestScene(selfTest: true)),
        new("particles", "Particles", "CPU and compute shader simulation side by side.", () => new ParticleTestScene()),
    ];

    public static TestDefinition? Find(string id) =>
        Tests.FirstOrDefault(test => test.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
