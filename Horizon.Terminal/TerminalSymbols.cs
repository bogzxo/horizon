namespace Horizon.UI.Components;

/// <summary>Directional TUI marks use the bundled symbol atlas, independent of the selected monospace font.</summary>
internal static class TerminalSymbols
{
    internal static string? IconOf(int codePoint) => codePoint < 0x2000 ? null : codePoint switch
    {
        0x2190 => "arrow-left",
        0x2191 => "arrow-up",
        0x2192 or 0x2794 => "arrow-right",
        0x2193 => "arrow-down",
        0x276F => "chevron-right",
        0x276E => "chevron-left",
        0x25B2 or 0x25B4 => "triangle-up",
        0x25BC or 0x25BE => "triangle-down",
        0x25B6 or 0x25B8 or 0x25BA or 0x2023 or 0x23F5 or 0x27A4 => "triangle-right",
        0x25C0 or 0x25C2 or 0x25C4 or 0x23F4 => "triangle-left",
        _ => null
    };
}
