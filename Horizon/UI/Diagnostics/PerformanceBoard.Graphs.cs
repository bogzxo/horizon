using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI;

// The graphs of the board, both drawn out of a loop's timeline (see LoopStatistics.CopyTimeline).
internal sealed partial class PerformanceBoard
{
    // How many times what is usual the top of a graph is, see Timeline
    private const float HEADROOM = 2.5f;

    // The round numbers a graph's top can be, times a power of ten. An array made once, a span written out in
    // the method made a little something on the heap every time in a Debug build, a hundred and twenty times
    // a second, in the overlay that counts the garbage
    private static readonly float[] Steps = [1.0f, 1.5f, 2.0f, 3.0f, 5.0f, 7.5f];

    /// <summary>
    /// Helper method to draw the last twelve seconds of a loop, a bar for every tenth of a second. The solid part of
    /// a bar is how long a turn took on average in that tenth and the pale part on top of it how long the worst one
    /// did, so a steady loop is a flat band and a hitch is a spike standing out of it, in the colour of how bad it
    /// was. The top of the graph is a round number two and a half times what is usual (the slice in the middle of
    /// them all, see <see cref="PerformanceSample.FrameUsualMs"/>), written in its corner, so the
    /// band sits in the lower third whatever the frame rate is. It is not the worst there is. One frame of three
    /// hundred milliseconds while a scene loads and twelve seconds of graph are a flat line with a pole in it. A
    /// spike that goes off the top is cut off there, the number under the graph says how long it really was.
    /// </summary>
    /// <param name="gaps">Whether it is how far apart the turns came that is drawn (the frames, as the eye gets them) or how long they worked for (the ticks, against their budget).</param>
    /// <param name="usual">What a turn usually takes, in milliseconds.</param>
    /// <param name="line">Where a dashed line is drawn across, in milliseconds, 0 for none.</param>
    /// <param name="budget">What a turn may take before its bar changes colour, in milliseconds.</param>
    /// <param name="parts">The first of the numbers the graph names its parts by, it takes two hundred of them.</param>
    private void Timeline(UIDrawList list, UIRect rect, LoopSlice[] slices, int filled, bool gaps, float usual, double line, double budget, int parts)
    {
        const float INSET = 4.0f;

        part = parts;
        Round(list, rect, 5.0f, WellColour);

        int first = slices.Length - filled;
        float ceiling = Ceiling(MathF.Max(usual * HEADROOM, 0.05f));

        float bottom = rect.Min.Y + INSET, room = rect.Height - INSET * 2.0f;
        float left = rect.Min.X + INSET, span = rect.Width - INSET * 2.0f;

        // Halfway up, for the eye to hold on to
        Fill(list, Box(left, MathF.Round(bottom + room * 0.5f) + 1.0f, span, 1.0f), RuleColour);

        float step = span / slices.Length;
        for (int i = first; i < slices.Length; i++)
        {
            float mean = gaps ? slices[i].GapMs : slices[i].WorkMs;
            float peak = gaps ? slices[i].WorstGapMs : slices[i].WorstWorkMs;
            if (peak <= 0.0f)
                continue;

            // On whole pixels with one left empty between two bars, or they smear into each other
            float from = MathF.Round(left + i * step);
            float to = MathF.Max(from + 1.0f, MathF.Round(left + (i + 1) * step) - 1.0f);

            float solid = MathF.Max(1.0f, MathF.Round(MathF.Min(1.0f, mean / ceiling) * room));
            float whole = MathF.Round(MathF.Min(1.0f, peak / ceiling) * room);

            // A part to every place along the graph, its bar first and then its spike if it has one. A bar that
            // was "the 40th quad of the graph" was blended with its neighbour for a tick every time a spike
            // came or went anywhere to its left, and with a graph that is still filling up that is every time
            list.BeginPart(parts + 10 + i);
            list.Rect(new UIRect(new Vector2(from, bottom), new Vector2(to, bottom + solid)), Against(mean, budget, Accent) with { W = 0.9f });

            if (whole > solid)
            {
                Vector4 spike = Against(peak, budget, Accent);
                list.Rect(new UIRect(new Vector2(from, bottom + solid), new Vector2(to, bottom + whole)), spike with { W = spike == Accent ? 0.36f : 0.95f });
            }
        }

        // The line across, one part however many dashes it is, they are always as many
        part = parts + 150;
        if (line > 0.0 && line <= ceiling)
        {
            float y = MathF.Round(bottom + (float)(line / ceiling) * room);

            list.BeginPart(part);
            for (float x = left; x < left + span - 2.0f; x += 7.0f)
                list.Rect(Box(MathF.Round(x), y + 1.0f, 4.0f, 1.0f), new Vector4(1.0f, 1.0f, 1.0f, 0.42f));
        }

        // What the top of it is, on a patch of the card so a spike going past under it doesn't make it unreadable
        part = parts + 160;
        ink.Start().Add(ceiling, "0.##").Add(" ms");
        var label = Box(rect.Min.X + 4.0f, rect.Max.Y - 3.0f, Width(ink.Text, small) + 8.0f, 15.0f);
        Round(list, label, 4.0f, CardColour with { W = 0.72f });
        Words(list, ink.Text, label, Origin.Center, small, Dim);
    }

    /// <summary>
    /// Helper method to draw the little graph of the minimal overlay, the worst frame of every tenth of the last
    /// few seconds. Flat is smooth, anything sticking up was felt.
    /// </summary>
    /// <param name="parts">The first of the numbers it names its parts by.</param>
    private void Spark(UIDrawList list, UIRect rect, int parts)
    {
        const float BAR = 2.0f;

        var slices = Sample.Frames;
        int bars = (int)(rect.Width / BAR);
        int filled = Math.Min(Sample.FrameSlices, bars);
        int first = slices.Length - filled;

        float ceiling = Ceiling(MathF.Max(Sample.FrameUsualMs * HEADROOM, 0.05f));
        double budget = Sample.FrameBudgetMs;

        part = parts;
        Fill(list, Box(rect.Min.X, rect.Min.Y + 1.0f, rect.Width, 1.0f), RuleColour);

        // The newest against the right hand side, what there is none of yet is left empty on the left
        for (int i = first; i < slices.Length; i++)
        {
            float peak = slices[i].WorstGapMs;
            if (peak <= 0.0f)
                continue;

            float from = rect.Max.X - (slices.Length - i) * BAR;
            float height = MathF.Max(1.0f, MathF.Round(MathF.Min(1.0f, peak / ceiling) * rect.Height));

            // Named by its place, like the bars of the big graphs
            list.BeginPart(parts + 10 + i);
            list.Rect(new UIRect(new Vector2(from, rect.Min.Y), new Vector2(from + BAR - 1.0f, rect.Min.Y + height)), Against(peak, budget, Accent));
        }
    }

    /// <summary>
    /// The next round number up from a value, for the top of a graph. 1, 1.5, 2, 3, 5 or 7.5 times a power of ten,
    /// a graph whose top is 3.71 ms tells nobody anything and one that only knows 1, 2 and 5 is twice too tall half
    /// of the time.
    /// </summary>
    internal static float Ceiling(float value)
    {
        // Every power of ten worked out afresh and with a hair to spare. Ten times a hundredth ten times over is
        // not quite one in floats, and one millisecond was being rounded up to one and a half
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
}
