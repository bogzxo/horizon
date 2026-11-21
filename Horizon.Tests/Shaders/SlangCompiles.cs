namespace Horizon.Tests;

/// <summary>
/// The tests that compile shaders with slang, one class at a time after the rest. Two of them compiling at once had
/// slang's own temporary .slang file taken by the other now and then, "being used by another process", and an inline
/// program that compiles fine failed to.
/// </summary>
[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class SlangCompiles
{
    public const string NAME = "slang compiles";
}
