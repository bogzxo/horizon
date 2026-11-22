using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The box over a call being typed, how the function is called with the argument the caret is on picked out, which of its
// overloads it is and what it does. It was the call written edge to edge in a box half a line taller than the text, the
// argument told apart only by bold, the "1 of 3" run into the call in the same colour and the docs straight under it.
// Now there's room round the call, the argument is the highlight's colour with a line under it, the counter is dim and
// the docs are under a rule.
public partial class CodeEditor
{
    // The room round what the box says, across and up and down, and the gap between the call and the docs either side of
    // the rule between them
    private const float SIGNATURE_PAD_X = TEXT_PAD * 1.5f, SIGNATURE_PAD_Y = TEXT_PAD, SIGNATURE_GAP = TEXT_PAD;

    private readonly List<(int Start, int Length)> signatureLabelLines = [];
    private readonly List<(int Start, int Length)> signatureDocLines = [];
    private string signaturePopupLabel = string.Empty, signaturePopupDocs = string.Empty;
    private (string? Label, string? Counter, string? Summary, string? Parameter, UIFont? Code, UIFont? Prose, float Scale, Vector2 Room) signaturePopupFor;
    private Vector2 signaturePopupSize;

    // Cache wrapping while the same help is painted; native documentation can be several paragraphs long.
    private Vector2 SignaturePopupSize(UISignature said, string counter, UIFont codeFont, UIFont proseFont, float scale, Vector2 room)
    {
        var key = (said.Text, counter, said.Documentation, said.ParameterDocumentation, codeFont, proseFont, scale, room);
        if (signaturePopupFor == key) return signaturePopupSize;
        signaturePopupFor = key;
        signaturePopupLabel = counter + said.Text;
        string summary = said.Documentation ?? string.Empty, parameter = said.ParameterDocumentation ?? string.Empty;
        signaturePopupDocs = parameter.Length == 0 ? summary : summary.Length == 0 || parameter == summary ? parameter : parameter + "\n\n" + summary;
        float codeLine = codeFont.LineHeight * scale, proseLine = proseFont.LineHeight * scale;
        float natural = codeFont.Measure(signaturePopupLabel, scale, markup: false).X + SIGNATURE_PAD_X * 2 + codeFont.Measure(" ", scale, markup: false).X;
        float width = Math.Clamp(MathF.Max(natural, signaturePopupDocs.Length > 0 ? codeLine * 26 : 0), 1, MathF.Max(1, room.X));
        float heightLimit = MathF.Max(codeLine + SIGNATURE_PAD_Y * 2, room.Y * 0.55f);
        signatureLabelLines.Clear();
        signatureDocLines.Clear();
        codeFont.Wrap(signaturePopupLabel, scale, MathF.Max(1, width - SIGNATURE_PAD_X * 2), signatureLabelLines, markup: false);
        int labelMost = Math.Max(1, (int)((heightLimit - SIGNATURE_PAD_Y * 2) / codeLine));
        if (signatureLabelLines.Count > labelMost) signatureLabelLines.RemoveRange(labelMost, signatureLabelLines.Count - labelMost);
        float height = signatureLabelLines.Count * codeLine + SIGNATURE_PAD_Y * 2;
        if (signaturePopupDocs.Length > 0)
        {
            proseFont.Wrap(signaturePopupDocs, scale, MathF.Max(1, width - SIGNATURE_PAD_X * 2), signatureDocLines, markup: false);
            int most = Math.Clamp((int)((heightLimit - height - SIGNATURE_GAP * 2) / proseLine), 0, 8);
            if (signatureDocLines.Count > most) signatureDocLines.RemoveRange(most, signatureDocLines.Count - most);
            if (signatureDocLines.Count > 0) height += SIGNATURE_GAP * 2 + 1.0f + signatureDocLines.Count * proseLine;
        }
        return signaturePopupSize = new Vector2(width, height);
    }

    // The signature in a box over the row of the call's name (under it when that's the top row), clear of the row by a
    // little so it doesn't sit on the letters of the line over it
    private void PaintSignature(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, UISignature said)
    {
        if (said.Text.Length == 0)
            return;

        list.BeginPart(PART_SIGNATURE);
        float small = scale * 0.92f;
        UIFont codeFont = FontOf(skin), proseFont = skin.Font;

        // Which overload of how many in front of it, when it has others, the way Visual Studio says it
        var (index, count) = SignatureOverload;
        string counter = count > 1 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, OverloadCounter, index + 1, count) + "   " : string.Empty;
        Vector2 size = SignaturePopupSize(said, counter, codeFont, proseFont, small, area.Size);
        float width = size.X, height = size.Y;
        float clear = MathF.Round(lineHeight * 0.15f);
        int row = RowOf(signatureAt.Line, signatureAt.Column);
        float top = RowTop(area, row, lineHeight);
        float x = MathF.Max(area.Min.X, left + ColumnX(skin, scale, signatureAt.Line, signatureAt.Column) - SIGNATURE_PAD_X);
        x = Math.Clamp(x, area.Min.X, MathF.Max(area.Min.X, area.Max.X - width));
        float bottom = row > first ? top + clear : top - lineHeight - height - clear;
        if (bottom + height > area.Max.Y) bottom = top - lineHeight - height - clear;
        bottom = Math.Clamp(bottom, area.Min.Y, MathF.Max(area.Min.Y, area.Max.Y - height));

        var box = new UIRect(new Vector2(x, bottom), new Vector2(x + width, bottom + height));
        list.Popup(box, Module?.Compositor.PopupGlass);

        int start = Math.Clamp(said.Start, 0, said.Text.Length), length = Math.Clamp(said.Length, 0, said.Text.Length - start);
        start += counter.Length;
        Vector4 text = skin.TextColor with { W = skin.TextColor.W * 0.85f };
        Vector4 lit = skin.HighlightColor;
        float codeLine = codeFont.LineHeight * small;
        float textTop = box.Max.Y - SIGNATURE_PAD_Y;
        list.PushFont(codeFont);
        foreach (var line in signatureLabelLines)
        {
            ReadOnlySpan<char> all = signaturePopupLabel.AsSpan(line.Start, line.Length);
            int quiet = Math.Clamp(counter.Length - line.Start, 0, line.Length);
            int from = Math.Clamp(start - line.Start, quiet, line.Length), to = Math.Clamp(start + length - line.Start, from, line.Length);
            float pen = box.Min.X + SIGNATURE_PAD_X;

            // The counter dim, it's about the box and not the call
            if (quiet > 0)
            {
                list.Text(all[..quiet], new Vector2(pen, textTop), small, skin.DimTextColor, markup: false);
                pen += codeFont.Measure(all[..quiet], small, markup: false).X;
            }

            list.Text(all[quiet..from], new Vector2(pen, textTop), small, text, markup: false);
            pen += codeFont.Measure(all[quiet..from], small, markup: false).X;
            float wide = codeFont.Measure(all[from..to], small, markup: false).X + codeFont.BoldAdvance * small * (to - from);
            list.Text(all[from..to], new Vector2(pen, textTop), small, lit, false, UITextStyle.Bold, default);

            // A line under the argument the caret is on, so it isn't told apart by its weight alone. Always painted, clear
            // when there's none on the line, so the quads after it don't shift
            float under = MathF.Round(textTop - codeLine + MathF.Max(1.0f, codeLine * 0.08f));
            list.Rect(new UIRect(new Vector2(pen, under), new Vector2(pen + wide, under + MathF.Max(1.0f, MathF.Round(codeLine * 0.06f)))), lit with { W = to > from ? lit.W : 0.0f });
            pen += wide;
            list.Text(all[to..], new Vector2(pen, textTop), small, text, markup: false);
            textTop -= codeLine;
        }
        list.PopFont();
        if (signatureDocLines.Count > 0)
        {
            // A rule between how it's called and what it does
            textTop -= SIGNATURE_GAP;
            list.Rect(new UIRect(new Vector2(box.Min.X + SIGNATURE_PAD_X, textTop - 1.0f), new Vector2(box.Max.X - SIGNATURE_PAD_X, textTop)), skin.BorderColor);
            textTop -= 1.0f + SIGNATURE_GAP;
            var inside = new UIRect(new Vector2(box.Min.X + SIGNATURE_PAD_X, box.Min.Y + SIGNATURE_PAD_Y * 0.5f), new Vector2(box.Max.X - SIGNATURE_PAD_X, textTop));
            list.PushFont(proseFont);
            list.Text(signaturePopupDocs, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(signatureDocLines), inside,
                Horizon.Rendering.Origin.TopLeft, small, skin.DimTextColor, markup: false);
            list.PopFont();
        }
    }
}
