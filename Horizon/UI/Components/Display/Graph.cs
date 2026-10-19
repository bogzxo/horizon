using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// The last so many of something as bars, the newest on the right. How long the frames took, how many players
/// were on, what a stock did. Every bar is a value and, on top of it and paler, the worst there was in the same
/// stretch, so a steady thing is a flat band and a hitch is a spike standing out of it.
/// <code>
/// let frames = compositor.graph({ parent: card, size: vec(400, 54), bars: 120, unit: "ms" });
/// </code>
/// <code>
/// graph.Count = filled;                        // how many of its bars there is something for yet
/// for (int i = 0; i &lt; graph.Bars; i++)
///     graph.Set(i, average[i], worst[i]);
/// graph.Top = Graph.RoundUp(usual * 2.5f);     // what the top of it stands for
/// </code>
/// The top is what whoever feeds it says it is (<see cref="Top"/>) and not the tallest bar there is, one freak
/// value would squash everything else flat. A bar that goes off the top is cut off there. A <see cref="Line"/> is
/// drawn across where something is meant to stay under, and a bar that is over the <see cref="Budget"/> is the
/// colour of a warning, twice over it the colour of trouble. It makes no garbage being fed or drawn.
/// </summary>
public class Graph : UIComponent
{
    private const float INSET = 4.0f;
    private static readonly Vector2 DefaultSize = new(240.0f, 48.0f);

    // The round numbers the top of a graph can be, times a power of ten. An array made once, a span written out
    // in the method made a little something on the heap every time in a Debug build
    private static readonly float[] Steps = [1.0f, 1.5f, 2.0f, 3.0f, 5.0f, 7.5f];

    private float[] values = new float[120], peaks = new float[120];
    private readonly char[] topText = new char[24];

    /// <summary>How many bars it has room for, a hundred and twenty unless told otherwise.</summary>
    public int Bars
    {
        get => values.Length;
        set
        {
            int bars = Math.Clamp(value, 1, 4096);
            if (bars == values.Length) return;

            values = new float[bars];
            peaks = new float[bars];
            Count = Math.Min(Count, bars);
        }
    }

    /// <summary>How many of the bars there is something for, the newest ones, against the right hand side. The rest is left empty.</summary>
    public int Count { get; set; }

    /// <summary>What the top of the graph stands for. <see cref="RoundUp"/> makes a round number of one.</summary>
    public float Top { get; set; } = 1.0f;

    /// <summary>Where a dashed line is drawn across, 0 for none. One above the top isn't drawn.</summary>
    public float Line { get; set; }

    /// <summary>What a bar may come to before it changes colour, 0 for bars that never do.</summary>
    public float Budget { get; set; }

    /// <summary>What the values are counted in, written after the top of the graph in its corner. Empty for a graph that doesn't say what its top is.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Whether it sits in a box with a line halfway up, the way a graph on a card does. Off, it is only its bars, for a little one in a line of text.</summary>
    public bool Boxed { get; set; } = true;

    /// <summary>How wide a bar is with the gap after it, in pixels, 0 for as wide as it takes for all of them to fill the graph.</summary>
    public float BarWidth { get; set; }

    /// <summary>Whether only the worst of every bar is drawn, as one solid bar, and the value left out.</summary>
    public bool PeaksOnly { get; set; }

    /// <summary>The colour of a bar that is within its budget, or null for the skin's accent.</summary>
    public Vector4? Color { get; set; }

    /// <summary>The colour of one that is over it, and of one that is over it twice.</summary>
    public Vector4 WarnColor { get; set; } = new(1.0f, 0.74f, 0.30f, 1.0f);

    /// <inheritdoc cref="WarnColor"/>
    public Vector4 BadColor { get; set; } = new(1.0f, 0.40f, 0.42f, 1.0f);

    /// <summary>
    /// Sets a bar, the first being the oldest.
    /// </summary>
    /// <param name="value">What it stands for.</param>
    /// <param name="peak">The worst there was in the same stretch, drawn paler on top. The value again for none.</param>
    public void Set(int bar, float value, float peak)
    {
        if ((uint)bar >= (uint)values.Length) return;

        values[bar] = value;
        peaks[bar] = MathF.Max(value, peak);
    }

    /// <summary>
    /// The next round number up from a value, for the top of a graph. 1, 1.5, 2, 3, 5 or 7.5 times a power of ten,
    /// a graph whose top is 3.71 tells nobody anything and one that only knows 1, 2 and 5 is twice too tall half
    /// of the time.
    /// </summary>
    public static float RoundUp(float value)
    {
        // Every power of ten worked out afresh and with a hair to spare. Ten times a hundredth ten times over is
        // not quite one in floats, and one was being rounded up to one and a half
        for (int power = -2; power <= 6; power++)
        {
            float decade = MathF.Pow(10.0f, power);
            foreach (float step in Steps)
            {
                if (value <= decade * step * 1.0001f) return decade * step;
            }
        }

        return value;
    }

    private Vector4 Against(float value, Vector4 fine) =>
        Budget <= 0.0f || value <= Budget ? fine : value <= Budget * 2.0f ? WarnColor : BadColor;

    protected override Vector2 Measure(UISkin skin) => DefaultSize;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect rect = Bounds;
        Vector4 fine = Color ?? skin.AccentColor;
        float inset = Boxed ? INSET : 0.0f;
        float top = MathF.Max(Top, 0.0001f);

        float bottom = rect.Min.Y + inset, room = rect.Height - inset * 2.0f;
        float left = rect.Min.X + inset, span = rect.Width - inset * 2.0f;

        list.BeginPart(0);
        if (Boxed)
        {
            list.RoundRect(rect, 5.0f, skin.TextColor with { W = 0.055f });

            // Halfway up, for the eye to hold on to
            list.BeginPart(1);
            list.Rect(new UIRect(new Vector2(left, MathF.Round(bottom + room * 0.5f)), new Vector2(left + span, MathF.Round(bottom + room * 0.5f) + 1.0f)), skin.TextColor with { W = 0.075f });
        }
        else
        {
            // What the bars stand on
            list.Rect(new UIRect(new Vector2(left, bottom), new Vector2(left + span, bottom + 1.0f)), skin.TextColor with { W = 0.075f });
        }

        int bars = values.Length;
        float step = BarWidth > 0.0f ? BarWidth : span / bars;

        // The newest against the right hand side. With bars of a width of their own that is where they start from,
        // and only as many as fit are drawn
        int shown = BarWidth > 0.0f ? Math.Min(bars, (int)(span / step)) : bars;
        int first = Math.Max(bars - Math.Min(Count, shown), 0);
        float start = left + span - shown * step;

        for (int i = first; i < bars; i++)
        {
            float mean = values[i], peak = peaks[i];
            if (peak <= 0.0f)
                continue;

            // On whole pixels with one left empty between two bars, or they smear into each other
            int place = i - (bars - shown);
            float from = MathF.Round(start + place * step);
            float to = MathF.Max(from + 1.0f, MathF.Round(start + (place + 1) * step) - 1.0f);

            float whole = MathF.Max(1.0f, MathF.Round(MathF.Min(1.0f, peak / top) * room));

            // A part to every place along the graph, its bar and then its spike if it has one. A bar that was
            // "the 40th quad of the graph" was blended with its neighbour for a tick every time a spike came or
            // went anywhere to its left, and with a graph that is still filling up that is every time
            list.BeginPart(10 + i);

            if (PeaksOnly)
            {
                list.Rect(new UIRect(new Vector2(from, bottom), new Vector2(to, bottom + whole)), Against(peak, fine));
                continue;
            }

            float solid = MathF.Max(1.0f, MathF.Round(MathF.Min(1.0f, mean / top) * room));
            list.Rect(new UIRect(new Vector2(from, bottom), new Vector2(to, bottom + solid)), Against(mean, fine) with { W = 0.9f });

            if (whole > solid)
            {
                Vector4 spike = Against(peak, fine);
                list.Rect(new UIRect(new Vector2(from, bottom + solid), new Vector2(to, bottom + whole)), spike with { W = spike == fine ? 0.36f : 0.95f });
            }
        }

        // The line across, one part however many dashes it is, they are always as many
        list.BeginPart(10 + bars);
        if (Line > 0.0f && Line <= top)
        {
            float y = MathF.Round(bottom + Line / top * room);
            for (float x = left; x < left + span - 2.0f; x += 7.0f)
                list.Rect(new UIRect(new Vector2(MathF.Round(x), y), new Vector2(MathF.Round(x) + 4.0f, y + 1.0f)), skin.TextColor with { W = 0.42f });
        }

        // What the top of it is, on a patch of what is behind it so a spike going past under it doesn't make it unreadable
        if (Boxed && Unit.Length > 0)
        {
            int length = 0;
            if (top.TryFormat(topText, out int digits, "0.##")) length = digits;
            if (length < topText.Length) topText[length++] = ' ';
            int unit = Math.Min(Unit.Length, topText.Length - length);
            Unit.AsSpan(0, unit).CopyTo(topText.AsSpan(length));
            length += unit;

            ReadOnlySpan<char> words = topText.AsSpan(0, length);
            float scale = skin.TextScale;
            float width = skin.Font.Measure(words, scale, markup: false).X + 8.0f;
            var label = new UIRect(new Vector2(rect.Min.X + 4.0f, rect.Max.Y - 18.0f), new Vector2(rect.Min.X + 4.0f + width, rect.Max.Y - 3.0f));

            list.BeginPart(11 + bars);
            list.RoundRect(label, 4.0f, skin.PanelColor with { W = 0.72f });

            list.BeginPart(12 + bars);
            list.Text(words, label, Origin.Center, scale, skin.TextColor with { W = 0.62f }, markup: false);
        }

        list.EndParts();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("bars", () => Bars, value => Bars = (int)value);
        Expose("top", () => Top, value => Top = value);
        Expose("line", () => Line, value => Line = value);
        Expose("budget", () => Budget, value => Budget = value);
        Expose("unit", () => Unit, value => Unit = value);
        Expose("boxed", () => Boxed, value => Boxed = value);
        Expose("bar_width", () => BarWidth, value => BarWidth = MathF.Max(0.0f, value));
        Expose("peaks_only", () => PeaksOnly, value => PeaksOnly = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
    }
}
