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
        new("fluid", "Fluid particles", "Physics particles that stack, level out and overflow from basin to basin.", () => new FluidTestScene()),
        new("lighting", "Lighting", "Deferred lights, shadows, normal maps and emissive particles.", () => new LightingTestScene()),
        new("renderer2d", "Renderer2D", "The same scene unlit, drawn at a quarter of the size and blown up.", () => new LightingTestScene(deferred: false)),
    ];

    public static TestDefinition? Find(string id) =>
        Tests.FirstOrDefault(test => test.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
