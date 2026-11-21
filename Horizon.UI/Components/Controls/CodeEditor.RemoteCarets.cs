using System.Numerics;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>A participant's caret, selection anchor and optional mouse position in zero-based document coordinates.</summary>
public readonly record struct UICodeRemoteCaret(string Id, string Label, int Line, int Column, int AnchorLine, int AnchorColumn,
    Vector4 Colour, int? PointerLine = null, int? PointerColumn = null);

public partial class CodeEditor
{
    /// <summary>The participants visible in this document. Replace this snapshot to update or remove them.</summary>
    public IReadOnlyList<UICodeRemoteCaret> RemoteCarets { get; set; } = [];

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
        for (int i = 0; i < RemoteCarets.Count; i++)
        {
            UICodeRemoteCaret remote = RemoteCarets[i];
            list.BeginPart(HashCode.Combine(nameof(RemoteCarets), remote.Id));
            if (RemotePoint(remote.Line, remote.Column) is { } caret && caret.X >= area.Min.X && caret.X <= area.Max.X)
            {
                list.Rect(new UIRect(caret - new Vector2(0, lineHeight), caret + new Vector2(2, 0)), remote.Colour);
                string label = string.IsNullOrEmpty(remote.Label) ? remote.Id : remote.Label;
                if (!string.IsNullOrEmpty(label))
                {
                    float small = scale * 0.65f;
                    float width = Width(skin, small, label) + 4;
                    float x = Math.Clamp(caret.X, area.Min.X, Math.Max(area.Min.X, area.Max.X - width));
                    float top = Math.Min(area.Max.Y, caret.Y + lineHeight * 0.65f);
                    list.Rect(new UIRect(new(x, top - lineHeight * 0.65f), new(x + width, top)), remote.Colour);
                    list.Text(label, new Vector2(x + 2, top), small, new Vector4(0, 0, 0, 1), markup: false);
                }
            }

            if (remote.PointerLine is { } line && remote.PointerColumn is { } column && RemotePoint(line, column) is { } pointer)
            {
                list.BeginPart(HashCode.Combine(nameof(RemoteCarets), remote.Id, nameof(remote.PointerColumn)));
                // A triangular pointer tip stays distinguishable when the caret and mouse share a column.
                Vector2 tip = pointer - new Vector2(0, lineHeight * 0.5f);
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
