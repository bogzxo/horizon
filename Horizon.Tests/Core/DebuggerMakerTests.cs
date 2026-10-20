#if DEBUG
using System.Reflection;
using System.Runtime.CompilerServices;

using Horizon.Engine;

namespace Horizon.Tests.Core;

/// <summary>The engine can't name the Skyline debugger, the UI says how one is made as it loads.</summary>
public class DebuggerMakerTests
{
    [Fact]
    public void Loading_the_UI_says_how_a_debugger_is_made()
    {
        RuntimeHelpers.RunModuleConstructor(Assembly.Load("Horizon.UI").ManifestModule.ModuleHandle);
        Assert.NotNull(GameEngine.DebuggerMaker);
    }
}
#endif
