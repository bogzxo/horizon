using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class TerminalSymbolTests
{
    [Theory]
    [InlineData('▶', "triangle-right")]
    [InlineData('▸', "triangle-right")]
    [InlineData('▼', "triangle-down")]
    [InlineData('▲', "triangle-up")]
    [InlineData('◀', "triangle-left")]
    [InlineData('→', "arrow-right")]
    [InlineData('←', "arrow-left")]
    [InlineData('↑', "arrow-up")]
    [InlineData('↓', "arrow-down")]
    [InlineData('❯', "chevron-right")]
    [InlineData('⏵', "triangle-right")]
    public void Tui_symbols_use_cell_sized_icons_without_changing_terminal_text(char symbol, string icon)
    {
        Assert.Equal(icon, TerminalSymbols.IconOf(symbol));
        var screen = new TerminalScreen(20, 2);
        screen.Feed("A" + symbol + "B");
        Assert.Equal(3, screen.CursorX);
        Assert.Equal(symbol, screen.Grid[0].Cells[1].CodePoint);
        Assert.Null(TerminalSymbols.IconOf('A'));
    }
}
