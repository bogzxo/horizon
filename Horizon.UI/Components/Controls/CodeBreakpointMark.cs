namespace Horizon.UI.Components;

/// <summary>What's different about a breakpoint in a <see cref="CodeEditor"/>'s gutter, the way it's drawn.</summary>
[Flags]
public enum CodeBreakpointMark
{
    /// <summary>The plain red dot.</summary>
    Plain = 0,

    /// <summary>It has a condition or a hit count, a dot with two bars across it.</summary>
    Conditional = 1,

    /// <summary>It logs a message rather than stopping, a diamond.</summary>
    Log = 2,

    /// <summary>It's there but off, the outline of whichever it is.</summary>
    Disabled = 4
}
