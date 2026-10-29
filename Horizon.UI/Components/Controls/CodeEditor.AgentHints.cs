using System.Numerics;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>An agent annotation anchored to a 0-based source position. It never becomes part of the source.</summary>
public readonly record struct UICodeAgentHint(string Id, int Line, int Column, string Text);

public partial class CodeEditor
{
    private IReadOnlyList<UICodeAgentHint> agentHints = [];
    private int agentHintsVersion;
    private readonly List<(string Id, string Text, UIRect Box, UIRect Close)> agentHintBoxes = [];

    /// <summary>Independent of diagnostics, language inlays and code lenses. Invalidated by any source edit.</summary>
    public IReadOnlyList<UICodeAgentHint> AgentHints
    {
        get => agentHintsVersion == document.Version ? agentHints : [];
        set
        {
            agentHints = value ?? [];
            agentHintsVersion = document.Version;
            agentHintBoxes.Clear();
        }
    }

    /// <summary>The close control removes the hint and reports its id.</summary>
    public Action<string>? OnAgentHintDismissed { get; set; }

    public bool DismissAgentHint(string id)
    {
        if (!AgentHints.Any(hint => hint.Id == id)) return false;
        AgentHints = [.. AgentHints.Where(hint => hint.Id != id)];
        OnAgentHintDismissed?.Invoke(id);
        return true;
    }

    /// <summary>The close control's local bounds, for accessibility clients and pointer tests.</summary>
    public UIRect AgentHintDismissBounds(string id) => agentHintBoxes.FirstOrDefault(box => box.Id == id).Close;

    private bool PressAgentHint(Vector2 point)
    {
        if (agentHintsVersion != document.Version) return false;
        foreach (var hint in agentHintBoxes)
        {
            if (!hint.Close.Contains(point)) continue;
            return DismissAgentHint(hint.Id);
        }
        return false;
    }

    private bool AgentHintTooltip(Vector2 point)
    {
        if (agentHintsVersion != document.Version) return false;
        foreach (var hint in agentHintBoxes)
        {
            if (!hint.Box.Contains(point)) continue;
            TooltipMarkdown = false;
            Tooltip = hint.Text;
            return true;
        }
        return false;
    }

    private void PaintAgentHints(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, float digit, int first, int last)
    {
        agentHintBoxes.Clear();
        var ends = new Dictionary<int, float>();
        foreach (var hint in AgentHints)
        {
            if (hint.Line < first || hint.Line > last || hint.Line < 0 || hint.Line >= document.LineCount || IsHidden(hint.Line, out _)) continue;
            int length = document.LineAt(hint.Line).Length;
            int column = Math.Clamp(hint.Column, 0, length);
            float top = TopOf(area, hint.Line, column, lineHeight);
            float anchor = left + ColumnX(skin, scale, hint.Line, column);
            list.Rect(new UIRect(new Vector2(anchor, top - lineHeight + 1), new Vector2(anchor + digit, top - lineHeight + 3)), skin.Palette.Link);
            float x = ends.GetValueOrDefault(hint.Line, left + EndX(skin, scale, hint.Line, length) + digit * 3
                + LensWidthOn(hint.Line, digit) + ArtWidthOn(hint.Line));
            foreach (var inline in inlineHints.Where(inline => inline.Line == hint.Line))
                x += EndHintWidth(skin, scale, inline.Text, digit);
            string caption = hint.Text.ReplaceLineEndings(" ");
            if (caption.Length > 64) caption = caption[..61] + "...";
            caption = "Agent: " + caption;
            float width = FontOf(skin).Measure(caption, scale, markup: false).X;
            float closeWidth = digit * 3;
            // Keep the dismiss control in sight even when the source line is wider than the viewport.
            x = MathF.Max(area.Min.X, MathF.Min(x, area.Max.X - width - closeWidth - digit));
            var box = new UIRect(new Vector2(x, top - lineHeight), new Vector2(x + width + closeWidth, top));
            var close = new UIRect(new Vector2(x + width, top - lineHeight), box.Max);
            list.Rect(box, skin.PanelColor with { W = 1 });
            list.Text(caption, new Vector2(x, top), scale, skin.Palette.Link, markup: false);
            list.Text(" ×", new Vector2(x + width, top), scale, skin.TextColor, markup: false);
            agentHintBoxes.Add((hint.Id, hint.Text, box, close));
            ends[hint.Line] = box.Max.X + digit;
        }
    }
}
