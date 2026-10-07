using System.Numerics;

using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// HIDL code to read, coloured in by what every bit of it is and with its lines numbered down the side.
/// It shows the code exactly as it is written, square brackets and all (a label would take those for icons).
/// It is as big as its code, so it goes inside of a scroll panel for anything longer than a few lines.
/// The colours are the skin's, see <see cref="UISyntaxColors"/>.
/// </summary>
public class CodeView : UIComponent
{
    // How many characters wide the gap between the line numbers and the code is
    private const int GUTTER_GAP = 2;

    private string text = string.Empty;
    private string[] lines = [];
    private List<HidlSpan>[] spans = [];

    /// <summary>The code. It is cut up for colouring once, when it is set.</summary>
    public string Text
    {
        get => text;
        set
        {
            value ??= string.Empty;
            if (value == text)
                return;

            text = value;
            lines = text.Replace("\r\n", "\n").Split('\n');
            spans = new List<HidlSpan>[lines.Length];

            for (int i = 0; i < lines.Length; i++)
                HidlHighlighter.Read(lines[i], spans[i] = []);
        }
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Whether the lines are numbered down the left.</summary>
    public bool LineNumbers { get; set; } = true;

    /// <summary>How many lines of code there are.</summary>
    public int LineCount => lines.Length;

    public CodeView()
    { }

    public CodeView(string text)
    {
        Text = text;
    }

    private float ScaleFor(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    /// <summary>
    /// Helper method to work out how wide the numbers down the side are with the gap after them, nothing if there are none.
    /// </summary>
    private float GutterWidth(UISkin skin, float scale)
    {
        if (!LineNumbers)
            return 0.0f;

        // Room for the biggest number there is, every digit of a font is as wide as its zero
        int digits = Math.Max(2, lines.Length.ToString().Length);
        return skin.Font.Measure("0", scale, markup: false).X * (digits + GUTTER_GAP);
    }

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = ScaleFor(skin);
        float widest = 0.0f;

        foreach (string line in lines)
            widest = MathF.Max(widest, skin.Font.Measure(line, scale, markup: false).X);

        return new Vector2(GutterWidth(skin, scale) + widest, skin.Font.LineHeight * scale * Math.Max(1, lines.Length)) + Padding.Total;
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UISyntaxColors colors = skin.Syntax;

        float scale = ScaleFor(skin);
        float lineHeight = skin.Font.LineHeight * scale;
        float gutter = GutterWidth(skin, scale);
        float digit = skin.Font.Measure("0", scale, markup: false).X;

        UIRect content = Bounds.Shrink(Padding);
        Span<char> number = stackalloc char[12];

        for (int i = 0; i < lines.Length; i++)
        {
            float top = content.Max.Y - i * lineHeight;

            if (LineNumbers && (i + 1).TryFormat(number, out int length))
            {
                // Lined up on their right, so the ones and the tens are under each other
                float right = content.Min.X + gutter - digit * GUTTER_GAP;
                list.Text(number[..length], new Vector2(right - digit * length, top), scale, colors.LineNumber, markup: false);
            }

            ReadOnlySpan<char> line = lines[i];

            float x = content.Min.X + gutter;
            int at = 0;

            foreach (HidlSpan span in spans[i])
            {
                // Past whatever whitespace there is between the last span and this one
                x += skin.Font.Measure(line[at..span.Start], scale, markup: false).X;

                ReadOnlySpan<char> piece = line.Slice(span.Start, span.Length);
                list.Text(piece, new Vector2(x, top), scale, colors.Of(span.Kind), markup: false);

                x += skin.Font.Measure(piece, scale, markup: false).X;
                at = span.Start + span.Length;
            }
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("line_numbers", () => LineNumbers, value => LineNumbers = value);
    }
}
