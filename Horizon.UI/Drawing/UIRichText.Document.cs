using System.Numerics;

namespace Horizon.UI.Drawing;

/// <summary>
/// Markdown laid out to be read as a page rather than squeezed into a tooltip, the way a browser lays a document out.
/// Lines have room between them, headings have room over them and the two biggest a rule under, code is in a font for
/// code with padding round it and rounded corners, task lists get boxes. Only when <see cref="Document"/> is on, a
/// label or a tooltip that says nothing stays as tight as it was.
/// </summary>
public sealed partial class UIRichText
{
    /// <summary>Whether it is laid out as a document to read, airy and padded. Off for the compact look a label wants.</summary>
    public bool Document { get; set; }

    /// <summary>
    /// The font code is written in, inline and in blocks, null for the font the rest is in. A monospace one keeps the
    /// columns of a block of code lined up.
    /// </summary>
    public UIFont? CodeFont { get; set; }

    // How far apart a document's lines are, as many times as tall as a line of the text, for prose, code and headings
    private const float BODY_SPACING = 1.55f;
    private const float CODE_SPACING = 1.3f;
    private const float HEADING_SPACING = 1.15f;

    // The gap an empty line leaves in a document, and the room over a heading and under it, in lines
    private const float PARAGRAPH_GAP = 0.7f;
    private const float HEADING_ABOVE = 1.1f;
    private const float HEADING_BELOW = 0.45f;

    // How big code is next to the text round it, a monospace face looks bigger than the same size of most others
    private const float CODE_SIZE = 0.9f;

    // A block of code's room round its text across and down, the gap round it and how round its corners are, in lines
    private const float CODE_PAD_X = 0.85f;
    private const float CODE_PAD_Y = 0.6f;
    private const float CODE_MARGIN = 0.7f;
    private const float CODE_BLOCK_RADIUS = 0.3f;

    // How faint a rule is, the ones under headings and the --- ones
    private const float RULE_ALPHA = 0.35f;

    // How far apart the lines going on are, see BODY_SPACING
    private float spacing = BODY_SPACING;

    private bool IsMono(in State style) => style.Code && CodeFont is not null;

    private UIFont FaceOf(bool mono) => mono ? CodeFont! : font;

    // Helper method for the scale code is written at so a line of it is about as tall as the text round it
    private float CodeScale(float scale) => scale * font.LineHeight / CodeFont!.LineHeight * CODE_SIZE;

    private float CodeRadius() => MathF.Round(font.LineHeight * baseScale * 0.18f);

    /// <summary>Helper method to put a bit of padding into the word at the edge of some inline code, the code's back runs under it.</summary>
    private void CodeSpacer(int at)
    {
        float scale = IsMono(state) ? CodeScale(baseScale * state.Size) : baseScale * state.Size;
        word.Add(new Pending { Start = at, Length = 0, Width = MathF.Round(font.LineHeight * scale * 0.2f), State = state, Scale = scale });
    }

    /// <summary>
    /// Helper method to make the backs of inline code that touch on the line going on one box, so a bit of code with
    /// spaces in it is one rounded box rather than a row of them.
    /// </summary>
    private void JoinCodeBacks()
    {
        int last = -1;
        for (int i = lineStartPiece; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];
            if (piece.Kind != Kind.Box || !piece.CodeBack || piece.Line != lineCount)
                continue;

            if (last >= 0)
            {
                Piece before = pieces[last];
                if (MathF.Abs(piece.X - (before.X + before.Width)) < 0.5f)
                {
                    before.Width = piece.X + piece.Width - before.X;
                    pieces[last] = before;
                    piece.Width = 0.0f;
                    pieces[i] = piece;
                    continue;
                }
            }

            last = i;
        }
    }

    private static void PaintBox(UIDrawList list, in Piece piece, UIRect rect, Vector4 ink)
    {
        if (piece.Outline)
            list.RoundOutline(rect, piece.Radius, MathF.Max(1.0f, MathF.Round(rect.Height * 0.14f)), ink);
        else if (piece.Radius > 0.0f)
            list.RoundRect(rect, piece.Radius, ink);
        else
            list.Rect(rect, ink);
    }

    /// <summary>Whether a bullet's words start with a task's box, [ ] or [x], and if so whether it is ticked. Null for an ordinary bullet.</summary>
    private static bool? TaskOf(ReadOnlySpan<char> words)
    {
        if (words.Length < 4 || words[0] != '[' || words[2] != ']' || words[3] != ' ')
            return null;

        return words[1] switch
        {
            ' ' => false,
            'x' or 'X' => true,
            _ => null
        };
    }

    /// <summary>Helper method to draw a task's box where a bullet would go, filled in the accent when it's ticked.</summary>
    private void AddTaskBox(float x, float em, bool done)
    {
        float side = MathF.Round(em * 0.6f);

        pieces.Add(new Piece
        {
            Kind = Kind.Box,
            Line = lineCount,
            X = x,
            Y = -MathF.Round(em * 0.32f + side * 0.5f),
            Width = side,
            Height = side,
            Shade = done ? Shade.Accent : Shade.Dim,
            Alpha = 1.0f,
            Link = -1,
            Block = -1,
            Radius = MathF.Round(side * 0.2f),
            Outline = !done
        });
    }

    /// <summary>
    /// Helper method to lay out a heading of a document, the sizes a browser has them at, room over it unless it opens
    /// the text, and a faint rule under the two biggest.
    /// </summary>
    private void LayOutDocumentHeading(int start, int end, int hashes, bool first)
    {
        float em = font.LineHeight * baseScale;
        if (!first) pendingGap = MathF.Max(pendingGap, em * HEADING_ABOVE);

        spacing = HEADING_SPACING;
        state.Size = hashes switch { 1 => 1.9f, 2 => 1.45f, 3 => 1.2f, 4 => 1.0f, 5 => 0.9f, _ => 0.85f };
        state.Style = UITextStyle.Bold;
        sizes.Add(state.Size);

        int from = start + hashes + 1;
        LayOutParagraphLine(from, end - from, 0.0f, 0.0f);

        if (hashes <= 2)
        {
            float gap = MathF.Round(em * 0.25f);
            float thickness = MathF.Max(1.0f, MathF.Round(em * 0.06f));

            fullWidth.Add(pieces.Count);
            pieces.Add(new Piece
            {
                Kind = Kind.Box,
                Line = -1,
                X = 0.0f,
                Y = cursorY + gap,
                Height = thickness,
                Shade = Shade.Dim,
                Alpha = RULE_ALPHA,
                Link = -1,
                Block = -1
            });

            cursorY += gap + thickness;
        }

        pendingGap = em * HEADING_BELOW;
    }
}
