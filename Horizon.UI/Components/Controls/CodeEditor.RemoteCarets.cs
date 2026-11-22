using System.Numerics;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>A participant's caret, selection anchor and optional mouse position in zero-based document coordinates.</summary>
public readonly record struct UICodeRemoteCaret(string Id, string Label, int Line, int Column, int AnchorLine, int AnchorColumn,
    Vector4 Colour, int? PointerLine = null, int? PointerColumn = null);

public partial class CodeEditor
{
    private IReadOnlyList<UICodeRemoteCaret> remoteCarets = [];
    private readonly Dictionary<string, long> remoteCaretMoved = new(StringComparer.Ordinal);

    /// <summary>The participants visible in this document. Replace this snapshot to update or remove them.</summary>
    public IReadOnlyList<UICodeRemoteCaret> RemoteCarets
    {
        get => remoteCarets;
        set
        {
            long now = Environment.TickCount64;
            if (value.Count == 0)
                remoteCaretMoved.Clear();
            else
            {
                foreach (UICodeRemoteCaret previous in remoteCarets)
                    if (!value.Any(next => next.Id == previous.Id))
                        remoteCaretMoved.Remove(previous.Id);
                foreach (UICodeRemoteCaret next in value)
                    if (!remoteCarets.Contains(next))
                        remoteCaretMoved[next.Id] = now;
            }
            remoteCarets = value;
        }
    }

    /// <summary>The text position under the mouse, or null when the pointer is outside this editor's text surface.</summary>
    public UITextPosition? PointerTextPosition
    {
        get
        {
            if (!IsHovered || Module is not { } module || paintedSkin is not { } skin)
                return null;
            Vector2 point = module.ToLocal(module.Compositor.Pointer.Position);
            return TextArea(skin, paintedScale).Contains(point) ? PositionAt(point) : null;
        }
    }

    private void PaintRemoteSelections(UIDrawList list, UISkin skin, float scale, float left, float top, float lineHeight, float digit, CodeRow row)
    {
        for (int i = 0; i < RemoteCarets.Count; i++)
        {
            UICodeRemoteCaret remote = RemoteCarets[i];
            var caret = new UITextPosition(remote.Line, remote.Column);
            var anchor = new UITextPosition(remote.AnchorLine, remote.AnchorColumn);
            if (caret == anchor || (uint)caret.Line >= document.LineCount || (uint)anchor.Line >= document.LineCount)
                continue;
            var (start, end) = caret < anchor ? (caret, anchor) : (anchor, caret);
            if (row.Line < start.Line || row.Line > end.Line)
                continue;
            list.BeginPart(HashCode.Combine(nameof(RemoteCarets), remote.Id, row.Line, row.Start));
            PaintSelected(list, skin, scale, left, top, lineHeight, digit, row, start, end,
                remote.Colour with { W = remote.Colour.W * 0.20f });
        }
    }

    private void PaintRemoteCarets(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, int last)
    {
        if (RemoteCarets.Count == 0)
            return;
        long now = Environment.TickCount64;
        Vector2? pointer = IsHovered && Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : null;
        for (int i = 0; i < RemoteCarets.Count; i++)
        {
            UICodeRemoteCaret remote = RemoteCarets[i];
            list.BeginPart(HashCode.Combine(nameof(RemoteCarets), remote.Id));
            if (RemotePoint(remote.Line, remote.Column) is { } caret && caret.X >= area.Min.X && caret.X <= area.Max.X)
            {
                list.Rect(new UIRect(caret - new Vector2(0, lineHeight), caret + new Vector2(2, 0)), remote.Colour);
                string label = string.IsNullOrEmpty(remote.Label) ? remote.Id : remote.Label;
                var mark = new UIRect(caret - new Vector2(5, lineHeight), caret + new Vector2(7, 0));
                float alpha = pointer is { } hover && mark.Contains(hover) ? 1 :
                    1 - Math.Clamp((now - remoteCaretMoved.GetValueOrDefault(remote.Id) - 1750) / 500f, 0, 1);
                if (label.Length != 0 && alpha > 0)
                {
                    float small = scale * 0.65f;
                    float width = Width(skin, small, label) + 4;
                    float x = Math.Clamp(caret.X, area.Min.X, Math.Max(area.Min.X, area.Max.X - width));
                    float height = lineHeight * 0.65f;
                    float bottom = caret.Y - lineHeight;
                    if (bottom - height < area.Min.Y)
                        bottom = Math.Min(area.Max.Y, caret.Y + height);
                    float top = bottom - height;
                    var foreground = UIPalette.Contrast(skin.TextColor, remote.Colour) >= UIPalette.Contrast(skin.BarTextColor, remote.Colour)
                        ? skin.TextColor : skin.BarTextColor;
                    list.Rect(new UIRect(new(x, top), new(x + width, bottom)), remote.Colour with { W = alpha });
                    list.Text(label, new Vector2(x + 2, bottom), small, foreground with { W = alpha }, markup: false);
                }
            }

            if (remote.PointerLine is { } line && remote.PointerColumn is { } column && RemotePoint(line, column) is { } remotePointer)
            {
                list.BeginPart(HashCode.Combine(nameof(RemoteCarets), remote.Id, nameof(remote.PointerColumn)));
                // A triangular pointer tip stays distinguishable when the caret and mouse share a column.
                Vector2 tip = remotePointer - new Vector2(0, lineHeight * 0.5f);
                for (int strip = 0; strip < 8; strip++)
                {
                    Vector2 bottom = tip - new Vector2(0, strip + 1);
                    list.Rect(new UIRect(bottom, bottom + new Vector2(1 + strip * 0.75f, 1)), remote.Colour);
                }
            }
        }

        Vector2? RemotePoint(int line, int column)
        {
            if ((uint)line >= document.LineCount || IsHidden(line, out _))
                return null;
            column = Math.Clamp(column, 0, document.LineAt(line).Length);
            int row = RowOf(line, column);
            return row < first || row > last ? null : new Vector2(left + ColumnX(skin, scale, line, column), RowTop(area, row, lineHeight));
        }
    }
}
