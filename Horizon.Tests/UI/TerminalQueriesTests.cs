using System.Numerics;

using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>What a program asks the screen and is told, the modes, the colours and the settings, and the two modes that are a program asking for something of the view.</summary>
public class TerminalQueriesTests
{
    private static string Asked(TerminalScreen screen, string question)
    {
        screen.Responses.Clear();
        screen.Feed(question);
        return screen.Responses.ToString();
    }

    [Fact]
    public void A_private_mode_is_said_set_reset_or_unknown()
    {
        var screen = new TerminalScreen(20, 5);
        Assert.Equal("\u001b[?2004;2$y", Asked(screen, "\u001b[?2004$p"));
        screen.Feed("\u001b[?2004h");
        Assert.Equal("\u001b[?2004;1$y", Asked(screen, "\u001b[?2004$p"));
        Assert.Equal("\u001b[?25;1$y", Asked(screen, "\u001b[?25$p"));
        Assert.Equal("\u001b[?31337;0$y", Asked(screen, "\u001b[?31337$p"));
        Assert.Equal("\u001b[4;2$y", Asked(screen, "\u001b[4$p"));
    }

    [Fact]
    public void Synchronised_output_is_a_mode_of_its_own()
    {
        var screen = new TerminalScreen(20, 5);
        screen.Feed("\u001b[?2026h");
        Assert.True(screen.SynchronizedOutput);
        Assert.Equal("\u001b[?2026;1$y", Asked(screen, "\u001b[?2026$p"));
        screen.Feed("\u001b[?2026l");
        Assert.False(screen.SynchronizedOutput);
    }

    [Fact]
    public void The_colours_asked_for_are_the_palettes()
    {
        var palette = new TerminalPalette { Background = new Vector4(1.0f, 0.0f, 0.5f, 1.0f), Foreground = Vector4.One };
        palette.Ansi[1] = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        var screen = new TerminalScreen(20, 5) { Palette = palette };

        Assert.Equal("\u001b]11;rgb:ffff/0000/8080\u001b\\", Asked(screen, "\u001b]11;?\u001b\\"));
        Assert.Equal("\u001b]10;rgb:ffff/ffff/ffff\u001b\\", Asked(screen, "\u001b]10;?\u0007"));
        Assert.Equal("\u001b]4;1;rgb:0000/ffff/0000\u001b\\", Asked(screen, "\u001b]4;1;?\u001b\\"));
    }

    [Fact]
    public void A_setting_asked_for_is_answered_or_said_unknown()
    {
        var screen = new TerminalScreen(20, 5);
        screen.Feed("\u001b[5 q");
        Assert.Equal("\u001bP1$r5 q\u001b\\", Asked(screen, "\u001bP$q q\u001b\\"));
        Assert.Equal("\u001bP1$r1;5r\u001b\\", Asked(screen, "\u001bP$qr\u001b\\"));
        Assert.Equal("\u001bP0$r\u001b\\", Asked(screen, "\u001bP$qz\u001b\\"));
    }

    [Fact]
    public void The_clipboard_is_set_through_whoever_draws_the_screen_and_never_read()
    {
        var screen = new TerminalScreen(20, 5);
        string? set = null;
        screen.Clipboard = text => set = text;

        screen.Feed("\u001b]52;c;" + Convert.ToBase64String("yanked line"u8.ToArray()) + "\u0007");
        Assert.Equal("yanked line", set);

        set = null;
        Assert.Equal(string.Empty, Asked(screen, "\u001b]52;c;?\u0007"));
        Assert.Null(set);
    }

    [Fact]
    public void A_view_only_lets_a_program_set_the_clipboard_when_allowed()
    {
        var view = new TerminalView();
        Assert.False(view.AllowClipboard);
        Assert.Same(view.Palette, view.Screen.Palette);
    }
}
