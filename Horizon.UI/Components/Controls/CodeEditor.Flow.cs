using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The code flowing rather than snapping when it is wrapped again. Wrapped round the picture behind it, every row the code
// scrolls past her is flowed again, and a word that went from the end of one row to the start of the next used to be
// there the tick after, the lines round her snapping about as it went by. Every word now remembers where it was drawn
// (by its line and the column it starts at, in the code's own space so the scroll doesn't count) and eases to where the
// wrap puts it. A word that wasn't in sight a moment ago, or would go further than a few lines, is put there, and so is
// everything after an edit, typing is never a step behind.
public partial class CodeEditor
{
    /// <summary>How quickly a word flows to where it's going unless told otherwise, see <see cref="FlowRate"/>.</summary>
    public const float FLOW_RATE = 16.0f;

    /// <summary>
    /// How quickly a word that the wrap moved flows to its new place, a share of the way left every second (16 has it
    /// most of the way there in a seventh of a second, 0 puts it there at once). See the top of CodeEditor.Flow.cs.
    /// </summary>
    public float FlowRate { get; set; } = FLOW_RATE;

    // Further than this many lines a word is put there, a jump to another part of the file isn't flowed
    private const float FLOW_FAR_LINES = 6.0f;

    private struct FlowWord
    {
        public Vector2 Shown;
        public int Seen;
    }

    // Where every word in sight was last drawn, the paint that was, how much time since, and the text that was of
    private readonly Dictionary<(int Line, int Column), FlowWord> flow = [];
    private int flowPaint, flowVersion = -1;
    private float flowTime, flowStep, flowFar;
    private bool flowSnap;

    // Once a paint, before any of the code is drawn
    private void BeginFlow(float lineHeight)
    {
        flowPaint++;
        flowStep = 1.0f - MathF.Exp(-MathF.Max(0.0f, FlowRate) * flowTime);
        flowTime = 0.0f;
        flowFar = lineHeight * FLOW_FAR_LINES;

        // Edited since, everything is put where it is, and all of the time when nothing is to flow
        flowSnap = document.Version != flowVersion || FlowRate <= 0.0f;
        flowVersion = document.Version;
    }

    // Once a paint, after it, the words that weren't drawn let go of now and then
    private void EndFlow()
    {
        if (flow.Count < 4096 && flowPaint % 240 != 0)
            return;

        foreach (var (key, word) in flow)
        {
            if (word.Seen != flowPaint) flow.Remove(key);
        }
    }

    // Where a word that wants to be at a place in the code's space is drawn this time
    private Vector2 Flow(int line, int column, Vector2 wanted)
    {
        ref FlowWord word = ref CollectionsMarshal.GetValueRefOrAddDefault(flow, (line, column), out bool known);
        if (!known || flowSnap || word.Seen < flowPaint - 1 || Vector2.DistanceSquared(word.Shown, wanted) > flowFar * flowFar)
        {
            word.Shown = wanted;
        }
        else if (word.Shown != wanted)
        {
            word.Shown += (wanted - word.Shown) * flowStep;
            if (Vector2.DistanceSquared(word.Shown, wanted) < 0.25f)
                word.Shown = wanted;
        }

        word.Seen = flowPaint;
        return word.Shown;
    }

    /// <summary>
    /// Draws a piece of a row word by word, each where it is flowing to (see the top of CodeEditor.Flow.cs), and says
    /// where the piece ends as it is laid out. Unwrapped code is drawn as it is, nothing flows there.
    /// </summary>
    /// <param name="column">Where in its line the piece starts.</param>
    /// <param name="left">The left of the code on screen.</param>
    /// <param name="origin">The top of the code on screen, scrolled, where the code's own space starts.</param>
    private float DrawFlowing(UIDrawList list, UISkin skin, float scale, int line, int column, ReadOnlySpan<char> text, float x, float top, Vector4 color, float left, float origin)
    {
        if (!wordWrap)
            return Draw(list, skin, scale, text, x, top, color);

        int at = 0;
        while (at < text.Length)
        {
            // The gap before a word, spaces and tabs, only moves what comes after it
            if (text[at] is ' ' or '\t')
            {
                int gap = at;
                while (gap < text.Length && text[gap] is ' ' or '\t') gap++;
                x += Width(skin, scale, text[at..gap]);
                at = gap;
                continue;
            }

            int end = at;
            while (end < text.Length && text[end] is not (' ' or '\t')) end++;

            ReadOnlySpan<char> word = text[at..end];
            Vector2 shown = Flow(line, column + at, new Vector2(x - left, origin - top));
            list.Text(word, new Vector2(left + shown.X, origin - shown.Y), scale, color, markup: false);

            x += FontOf(skin).Measure(word, scale, markup: false).X;
            at = end;
        }

        return x;
    }

    // Where the number of a line is drawn, flowing up and down with the line
    private float FlowTop(int line, float top, float origin) => wordWrap ? origin - Flow(line, -1, new Vector2(0.0f, origin - top)).Y : top;
}
