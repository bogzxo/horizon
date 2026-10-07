using Horizon.Engine;

namespace Horizon.Testing;

/// <summary>
/// One entry of the test selector.
/// </summary>
/// <param name="Id">What to pass on the command line to start straight in this test.</param>
/// <param name="Name">What the test's button says.</param>
/// <param name="Description">A line about what the test shows, written next to its button.</param>
/// <param name="Create">Makes a fresh scene every time the test is started.</param>
internal sealed record TestDefinition(string Id, string Name, string Description, Func<Scene> Create);
