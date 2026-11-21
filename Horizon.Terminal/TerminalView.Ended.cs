using System.Numerics;

using Horizon.Input;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// A pane whose program ended and stayed. It looked alive, the cursor still in it and nothing said, the only word of it
// a line in the status bar that went in a few seconds. Now the cursor goes, a bar along the bottom of the pane says how
// it ended in the colour the gutter gives a command that ended that way, and Enter starts it again, Windows Terminal's
// "press Enter to restart". The bar has room of its own under the cells, it covers nothing the program wrote.
public partial class TerminalView
{
    /// <summary>The code its program ended with while it stays ended, null while something runs in it or before anything did.</summary>
    public int? EndedWith { get; private set; }

    /// <summary>What the bar says for a code, "Exited with code 1". Asked once as it ends. Null for the words it comes with.</summary>
    public Func<int, string>? EndedWords { get; set; }

    /// <summary>What the bar says after that while it can be started again, the key that does it.</summary>
    public string RestartHint { get; set; } = "Press Enter to restart";

    /// <summary>Whether the bar is drawn when it ends. Off for a pane that says how it ended itself (a task's).</summary>
    public bool ShowEnded { get; set; } = true;

    /// <summary>What Enter in an ended pane does, the panel starting its profile in it again. Null for nothing.</summary>
    public Action<TerminalView>? OnRestart { get; set; }

    private string endedSaid = string.Empty;

    /// <summary>What the bar says of how it ended, empty while it hasn't.</summary>
    public string EndedSaid => endedSaid;

    // Whether the bar has room under the cells, the program ended, it's shown and the pane isn't Dawn's editor
    private bool EndedShown => EndedWith is not null && ShowEnded;

    private void MarkEnded(int code)
    {
        EndedWith = code;
        endedSaid = EndedWords?.Invoke(code) ?? (code == 0 ? "Exited" : $"Exited with code {code}");
        RequestFrame();
    }

    private void ClearEnded()
    {
        EndedWith = null;
        endedSaid = string.Empty;
    }

    /// <summary>Enter in an ended pane starts it again, true when it did. Every other key goes where it went.</summary>
    private bool EndedKey(in UIKeyPress press)
    {
        if (EndedWith is null || OnRestart is null || press.Key is not (Key.Enter or Key.KeypadEnter) || press.Control || press.Alt || press.Shift)
            return false;

        OnRestart(this);
        return true;
    }

    /// <summary>How tall the bar is, a line of the UI's caption and room round it, nothing while it isn't shown.</summary>
    private float EndedHeight(UISkin skin) => EndedShown ? MathF.Round(skin.Font.LineHeight * skin.TextSize(UITextRole.Caption) + 14.0f) : 0.0f;

    private void PaintEnded(UIDrawList list, TerminalPalette palette)
    {
        if (!EndedShown || EndedWith is not { } code)
            return;

        UISkin skin = list.Skin;
        UIFont font = skin.Font;
        float small = skin.TextSize(UITextRole.Caption), line = font.LineHeight * small, height = EndedHeight(skin);
        float pad = MathF.Max(PADDING, Radius * 0.85f);
        var bar = new UIRect(new Vector2(Bounds.Min.X + pad, Bounds.Min.Y + pad * 0.5f), new Vector2(Bounds.Max.X - pad - SideWidth, Bounds.Min.Y + pad * 0.5f + height));
        if (bar.Width <= line * 2.0f)
            return;

        // The colours of the marks in the gutter, green for a program that ended well and red for one that didn't
        Vector4 outcome = code == 0 ? palette.Ansi[2] : palette.Ansi[1];
        Vector4 card = Vector4.Lerp(palette.Background, palette.Foreground, 0.07f) with { W = 1.0f };
        Vector4 dim = Vector4.Lerp(palette.Foreground, palette.Background, 0.4f);
        float round = MathF.Min(Radius > 0.0f ? Radius * 0.6f : skin.CornerRadius, height * 0.5f);

        list.PushFont(font);
        list.BeginPart(-5);
        list.RoundRect(bar, round, card);
        list.Rect(new UIRect(new Vector2(bar.Min.X, bar.Min.Y + 4.0f), new Vector2(bar.Min.X + 3.0f, bar.Max.Y - 4.0f)), outcome);

        float side = MathF.Round(line);
        float x = bar.Min.X + 12.0f;
        list.Symbol(code == 0 ? "pass" : "error", new UIRect(new Vector2(x, bar.Center.Y - side * 0.5f), new Vector2(x + side, bar.Center.Y + side * 0.5f)), outcome);
        x += side + 8.0f;

        float baseline = MathF.Round(bar.Center.Y + line * 0.5f);
        list.Text(endedSaid.AsSpan(), new Vector2(x, baseline), small, palette.Foreground, false, default, default);
        if (OnRestart is not null && RestartHint.Length > 0)
        {
            x += font.Measure(endedSaid, small, markup: false).X + line;
            if (x + font.Measure(RestartHint, small, markup: false).X <= bar.Max.X - 8.0f)
                list.Text(RestartHint.AsSpan(), new Vector2(x, baseline), small, dim, false, default, default);
        }

        list.PopFont();
    }
}
