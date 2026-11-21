using System.Runtime.CompilerServices;

using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// The grammars' time for a line, a lot longer in the tests. It's time by the clock, and with every class of the tests
/// running at once on a busy machine a couple of thousand letters of JSON went over a quarter of a second now and then,
/// the read was cut short there, and a test comparing two reads of a long line, or looking for a TODO's colour, failed
/// for how busy the machine was. Nothing here tests the limit itself.
/// </summary>
internal static class GrammarTime
{
    // Before any test, a library's module initializer is what this is for here (CA2255 is about libraries for others)
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Lengthen() => TextMateHighlighter.LineLimit = TimeSpan.FromSeconds(10);
}
