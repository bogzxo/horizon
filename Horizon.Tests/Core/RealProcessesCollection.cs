namespace Horizon.Tests.Core;

/// <summary>
/// The tests that run real programs in pseudo terminals, one after the other and never next to anything else. A shell
/// read by how long it has been quiet (the marks through ConPTY) or a pager waited on for its page lose out to a
/// machine busy with every other test at once.
/// </summary>
[CollectionDefinition(NAME, DisableParallelization = true)]
public sealed class RealProcessesCollection
{
    public const string NAME = "real processes";
}
