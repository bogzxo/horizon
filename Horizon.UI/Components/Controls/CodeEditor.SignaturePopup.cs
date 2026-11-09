using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

public partial class CodeEditor
{
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
        float natural = codeFont.Measure(signaturePopupLabel, scale, markup: false).X + TEXT_PAD * 2 + codeFont.Measure(" ", scale, markup: false).X;
        float width = Math.Clamp(MathF.Max(natural, signaturePopupDocs.Length > 0 ? codeLine * 26 : 0), 1, MathF.Max(1, room.X));
        float heightLimit = MathF.Max(codeLine + TEXT_PAD, room.Y * 0.55f);
        signatureLabelLines.Clear();
        signatureDocLines.Clear();
        codeFont.Wrap(signaturePopupLabel, scale, MathF.Max(1, width - TEXT_PAD * 2), signatureLabelLines, markup: false);
        int labelMost = Math.Max(1, (int)((heightLimit - TEXT_PAD) / codeLine));
        if (signatureLabelLines.Count > labelMost) signatureLabelLines.RemoveRange(labelMost, signatureLabelLines.Count - labelMost);
        float height = signatureLabelLines.Count * codeLine + TEXT_PAD;
        if (signaturePopupDocs.Length > 0)
        {
            proseFont.Wrap(signaturePopupDocs, scale, MathF.Max(1, width - TEXT_PAD * 2), signatureDocLines, markup: false);
            int most = Math.Clamp((int)((heightLimit - height - TEXT_PAD) / proseLine), 0, 8);
            if (signatureDocLines.Count > most) signatureDocLines.RemoveRange(most, signatureDocLines.Count - most);
            if (signatureDocLines.Count > 0) height += TEXT_PAD + signatureDocLines.Count * proseLine;
        }
        return signaturePopupSize = new Vector2(width, height);
    }
}
