using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI;

/// <summary>
/// What a <see cref="PerformanceOverlay"/> looks like, one component that paints the whole of it out of a
/// <see cref="PerformanceSample"/>. A pill with the frame rate in it for the minimal one, a card of sections for
/// the lot (the frames and their timeline, the threads, the passes of the GPU as a bar, the device, the memory).
/// <para>
/// It is painted by hand rather than put together out of labels and panels for two reasons. A number in a label
/// is a string and a string is garbage, forty of them four times a second, in the one corner of the screen that
/// is there to say how much garbage the game makes. Here every number is written into the same few characters
/// (<see cref="PerformanceInk"/>) and drawn from there. And a dashboard wants its columns to line up to the pixel,
/// which is a lot of layout to talk a stack of panels into and a few numbers to write down.
/// </para>
/// <para>
/// Every section is one method that says how tall it is and, handed a list, draws itself, so what it measures
/// and what it paints can't drift apart. See <see cref="Lay"/>.
/// </para>
/// </summary>
internal sealed partial class PerformanceBoard : UIComponent
{
    /* The colours. Nothing in here is pure black with some alpha, a layer with effects on it loses those */

    private static readonly Vector4 CardColour = new(0.05f, 0.058f, 0.082f, 0.96f);
    private static readonly Vector4 EdgeColour = new(1.0f, 1.0f, 1.0f, 0.09f);
    private static readonly Vector4 ShadeColour = new(0.01f, 0.012f, 0.02f, 0.14f);
    private static readonly Vector4 WellColour = new(1.0f, 1.0f, 1.0f, 0.055f);
    private static readonly Vector4 RuleColour = new(1.0f, 1.0f, 1.0f, 0.075f);

    private static readonly Vector4 Bright = new(0.96f, 0.97f, 1.0f, 1.0f);
    private static readonly Vector4 Plain = new(0.80f, 0.83f, 0.90f, 1.0f);
    private static readonly Vector4 Dim = new(0.62f, 0.66f, 0.75f, 1.0f);

    private static readonly Vector4 Accent = new(0.38f, 0.62f, 1.0f, 1.0f);
    private static readonly Vector4 Good = new(0.36f, 0.86f, 0.55f, 1.0f);
    private static readonly Vector4 Warn = new(1.0f, 0.74f, 0.30f, 1.0f);
    private static readonly Vector4 Bad = new(1.0f, 0.40f, 0.42f, 1.0f);

    // The key that gets the rest, yellow so it is the one thing in there that reads at a glance
    private static readonly Vector4 KeyColour = new(1.0f, 0.86f, 0.3f, 1.0f);

    // What the passes of the GPU are told apart by, in the order they first come in a frame
    private static readonly Vector4[] PassColours =
    [
        new(0.38f, 0.62f, 1.0f, 1.0f),
        new(0.27f, 0.80f, 0.76f, 1.0f),
        new(0.67f, 0.54f, 0.98f, 1.0f),
        new(1.0f, 0.74f, 0.30f, 1.0f),
        new(0.98f, 0.50f, 0.69f, 1.0f),
        new(0.47f, 0.85f, 0.47f, 1.0f),
        new(1.0f, 0.59f, 0.35f, 1.0f),
        new(0.60f, 0.65f, 0.74f, 1.0f),
    ];

    /* How big things are, in units of the UI, which is a pixel each unless the overlay is scaled */

    private const float PAD = 14.0f;        // from the edge of the card to what is in it
    private const float GAP = 13.0f;        // between two sections, with a rule down the middle of it
    private const float COLUMN = 404.0f;    // how wide a column of sections is
    private const float RADIUS = 10.0f;
    private const float TITLE = 21.0f;      // the line a section is named on
    private const float ROW = 17.0f;        // a line of text
    private const float CELL = 32.0f;       // a number with what it is under it

    private const float PILL = 30.0f;       // how tall the minimal one is
    private const float SPARK = 72.0f;      // how wide its little graph is

    private readonly PerformanceInk ink = new(), tail = new();

    // The skin and the sizes of text it is being laid out with, see Lay
    private UISkin skin = null!;
    private float text, small, large, huge;

    // The most lines the passes of the GPU have had while this has been up. A card that grows and shrinks by a
    // line whenever a pass comes or goes hops about in its corner
    private int rowsHeld;

    // Which part of the board is being painted, see UIDrawList.BeginPart. A frame between two ticks blends every
    // quad with the quad that had its name the tick before, and a quad's name is who painted it and as which of
    // their quads. The board is one component that paints fifteen hundred of them, so left alone a bar of a graph
    // is "quad 412", and the moment anything before it is a quad more or less (a number grows a digit, a graph
    // gets its next bar) it is "quad 413" and gets blended with what its neighbour was. Every bar halfway to the
    // bar next to it for a tick, ten times a second, is a graph that flickers, worst while it is still filling
    // up and gets a bar longer every time. So everything painted is a part of its own, numbered by what it is.
    // The sections and the lines in them start at numbers of their own, so one coming or going moves nobody else
    private int part;

    private const int CARD_PARTS = 10, PILL_PARTS = 100;
    private const int HEADER_PARTS = 1000, FRAME_PARTS = 2000, THREAD_PARTS = 3000, GPU_PARTS = 4000, DEVICE_PARTS = 5000, MEMORY_PARTS = 6000;

    public PerformanceSample Sample { get; }

    /// <summary>Whether it is the pill or the card. Off is the overlay's business, there is nothing to paint then.</summary>
    public PerformanceDetail Detail { get; set; } = PerformanceDetail.Compact;

    /// <summary>What is written on the key cap, nothing for no cap.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>How tall the card may be, in its own units. One that would be taller puts its sections in two columns.</summary>
    public float MaxHeight { get; set; } = float.MaxValue;

    public PerformanceBoard(PerformanceSample sample) => Sample = sample;

    /// <summary>Forgets what it remembered of the last time it was up, for when the overlay changes what it shows.</summary>
    public void Reset() => rowsHeld = 0;

    protected override Vector2 Measure(UISkin skin) => Lay(null, skin, Vector2.Zero);

    protected override void Paint(UIDrawList list)
    {
        Lay(list, list.Skin, new Vector2(MathF.Round(Bounds.Min.X), MathF.Round(Bounds.Max.Y)));
        list.EndParts();
    }

    /// <summary>
    /// Helper method to lay the board out from its top left corner and, if there is a list, to draw it.
    /// </summary>
    /// <returns>How big it is.</returns>
    private Vector2 Lay(UIDrawList? list, UISkin skin, Vector2 corner)
    {
        this.skin = skin;
        text = skin.TextScale;

        // What things are called is written as big as the numbers and told apart by being dim. This font stops
        // reading under twelve pixels, at ten an f is a plus sign and waits are wails
        small = text;
        large = text * 1.3f;
        huge = text * 2.7f;

        if (Detail != PerformanceDetail.Full)
        {
            Vector2 pill = new(Compact(null, Vector2.Zero), PILL);
            if (list is not null)
            {
                part = CARD_PARTS;
                Card(list, Box(corner.X, corner.Y, pill.X, pill.Y), PILL * 0.5f);
                Compact(list, corner);
            }

            return pill;
        }

        float left = Header(null, default) + GAP + Frame(null, default) + GAP + Threads(null, default);
        float right = Gpu(null, default) + GAP + Device(null, default) + GAP + Memory(null, default);

        // One column down the side of the screen if the screen is tall enough for it, the way these things are
        // worn nowadays. Two side by side if not
        bool wide = left + GAP + right + PAD * 2.0f > MaxHeight;
        Vector2 size = wide
            ? new Vector2(COLUMN * 2.0f + PAD * 2.0f + GAP * 2.0f, MathF.Max(left, right) + PAD * 2.0f)
            : new Vector2(COLUMN + PAD * 2.0f, left + GAP + right + PAD * 2.0f);

        if (list is null)
            return size;

        part = CARD_PARTS;
        var card = Box(corner.X, corner.Y, size.X, size.Y);
        Card(list, card, RADIUS);

        var column = Box(card.Min.X + PAD, card.Max.Y - PAD, COLUMN, size.Y - PAD * 2.0f);
        Section(list, ref column, Header(list, column), rule: true);
        Section(list, ref column, Frame(list, column), rule: true);
        Section(list, ref column, Threads(list, column), rule: !wide);

        if (wide)
        {
            // A rule down the middle and the rest starts again from the top
            float middle = card.Min.X + PAD + COLUMN + GAP;
            part = CARD_PARTS + 9;
            Fill(list, new UIRect(new Vector2(middle, card.Min.Y + PAD), new Vector2(middle + 1.0f, card.Max.Y - PAD)), RuleColour);
            column = Box(middle + GAP, card.Max.Y - PAD, COLUMN, size.Y - PAD * 2.0f);
        }

        Section(list, ref column, Gpu(list, column), rule: true);
        Section(list, ref column, Device(list, column), rule: true);
        Section(list, ref column, Memory(list, column), rule: false);

        return size;
    }

    /// <summary>
    /// Helper method to move on down a column past a section that was just drawn, with a rule in the gap under it
    /// if another one follows.
    /// </summary>
    private void Section(UIDrawList list, ref UIRect column, float height, bool rule)
    {
        float under = column.Max.Y - height;
        if (rule)
        {
            float line = MathF.Round(under - GAP * 0.5f);

            // Named by the section it is under, which is whoever painted last
            part = part / 1000 * 1000 + 990;
            Fill(list, new UIRect(new Vector2(column.Min.X, line), new Vector2(column.Max.X, line + 1.0f)), RuleColour);
        }

        column = new UIRect(column.Min, new Vector2(column.Max.X, under - GAP));
    }

    /// <summary>
    /// Helper method to draw what everything sits on, a rounded card with a thin edge and a bit of shade around
    /// it. The shade is three rings one inside the other, the nearest thing to a shadow a UI made of quads has.
    /// </summary>
    private void Card(UIDrawList list, UIRect card, float radius)
    {
        for (int ring = 3; ring >= 1; ring--)
        {
            float spread = ring * 1.5f;
            var grown = new UIRect(card.Min - new Vector2(spread, spread + 1.0f), card.Max + new Vector2(spread, spread - 1.0f));
            Round(list, grown, radius + spread, ShadeColour);
        }

        Round(list, card, radius, CardColour);
        Ring(list, card, radius, 1.0f, EdgeColour);
    }

    /// <summary>
    /// The minimal one, a pill. A dot that says how the frames are doing, how many there are a second, what one
    /// costs the CPU and the GPU, the last few seconds of them as a little graph and the key that gets the rest.
    /// Every number has a field as wide as the widest it gets, or the pill would breathe in and out with them.
    /// </summary>
    /// <returns>How wide it is.</returns>
    private float Compact(UIDrawList? list, Vector2 corner)
    {
        var sample = Sample;
        float x = corner.X + 13.0f, top = corner.Y;
        part = PILL_PARTS;

        Round(list, Box(x, top - (PILL - 8.0f) * 0.5f, 8.0f, 8.0f), 4.0f, sample.Ready ? Health() : Dim);
        x += 8.0f + 9.0f;

        if (!sample.Ready)
        {
            Write(list, "starting up", Box(x, top, 200.0f, PILL), Origin.Left, text, Plain);
            return MathF.Ceiling(x + Width("starting up", text) + 14.0f - corner.X);
        }

        float field = Width("0000", large);
        ink.Start().Add(Math.Min(sample.Fps, 9999.0), "0");
        Write(list, ink.Text, Box(x, top, field, PILL), Origin.Right, large, Bright);
        x += field + 5.0f;

        Write(list, "fps", Box(x, top - Drop(large, small), 60.0f, PILL), Origin.Left, small, Dim);
        x += Width("fps", small);

        x = Split(list, x, top);
        x = Field(list, x, top, "cpu", sample.WorkMs);

        // A device that doesn't time its frames has nothing to put here
        if (sample.GpuMs > 0.0)
        {
            x = Split(list, x, top);
            x = Field(list, x, top, "gpu", sample.GpuMs);
        }

        x = Split(list, x, top);
        if (list is not null) Spark(list, Box(x, top - (PILL - 14.0f) * 0.5f, SPARK, 14.0f), PILL_PARTS + 200);
        x += SPARK;

        part = PILL_PARTS + 80;

        if (Key.Length > 0)
        {
            x += 11.0f;
            x += Cap(list, x, top - (PILL - 18.0f) * 0.5f);
            return MathF.Ceiling(x + 7.0f - corner.X);
        }

        return MathF.Ceiling(x + 13.0f - corner.X);
    }

    /// <summary>Helper method to draw a thin rule between two things in the pill, with room either side of it.</summary>
    private float Split(UIDrawList? list, float x, float top)
    {
        Fill(list, Box(MathF.Round(x + 10.0f), top - 8.0f, 1.0f, PILL - 16.0f), RuleColour);
        return x + 21.0f;
    }

    /// <summary>Helper method to draw what something is called, how many milliseconds it is and that those are milliseconds.</summary>
    private float Field(UIDrawList? list, float x, float top, string name, double milliseconds)
    {
        Write(list, name, Box(x, top, 60.0f, PILL), Origin.Left, small, Dim);
        x += Width(name, small) + 7.0f;

        float field = Width("00.0", text);
        ink.Start().Add(Math.Min(milliseconds, 999.0), milliseconds < 99.95 ? "0.0" : "0");
        Write(list, ink.Text, Box(x, top, field, PILL), Origin.Right, text, Bright);
        x += field + 4.0f;

        Write(list, "ms", Box(x, top - Drop(text, small), 30.0f, PILL), Origin.Left, small, Dim);
        return x + Width("ms", small);
    }

    /// <summary>Helper method to draw the key that gets more as the cap of a key, with its top left corner where it is told.</summary>
    /// <returns>How wide the cap is.</returns>
    private float Cap(UIDrawList? list, float x, float top)
    {
        float width = MathF.Ceiling(Width(Key, small) + 13.0f);
        if (list is null)
            return width;

        var cap = Box(x, top, width, 18.0f);
        Round(list, cap, 5.0f, new Vector4(1.0f, 1.0f, 1.0f, 0.09f));
        Ring(list, cap, 5.0f, 1.0f, new Vector4(1.0f, 1.0f, 1.0f, 0.13f));
        Words(list, Key, cap, Origin.Center, small, KeyColour);
        return width;
    }

    /* What the sections are drawn with */

    /// <summary>A rectangle by its top left corner and how big it is, which is how a layout that goes down the page thinks. The UI has its Y going up.</summary>
    private static UIRect Box(float left, float top, float width, float height) =>
        new(new Vector2(left, top - height), new Vector2(left + width, top));

    /* Every quad the board paints goes through one of these, which give it a part of its own first. See part */

    private void Write(UIDrawList? list, ReadOnlySpan<char> words, UIRect area, Origin align, float scale, Vector4 colour) =>
        Words(list, words, area, align, scale, colour);

    private void Words(UIDrawList? list, ReadOnlySpan<char> words, UIRect area, Origin align, float scale, Vector4 colour)
    {
        if (list is null) return;

        list.BeginPart(part++);
        list.Text(words, area, align, scale, colour, markup: false);
    }

    private void Words(UIDrawList? list, ReadOnlySpan<char> words, Vector2 topLeft, float scale, Vector4 colour)
    {
        if (list is null) return;

        list.BeginPart(part++);
        list.Text(words, topLeft, scale, colour, markup: false);
    }

    private void Fill(UIDrawList? list, UIRect rect, Vector4 colour)
    {
        if (list is null) return;

        list.BeginPart(part++);
        list.Rect(rect, colour);
    }

    private void Round(UIDrawList? list, UIRect rect, float radius, Vector4 colour)
    {
        if (list is null) return;

        list.BeginPart(part++);
        list.RoundRect(rect, radius, colour);
    }

    private void Ring(UIDrawList? list, UIRect rect, float radius, float thickness, Vector4 colour)
    {
        if (list is null) return;

        list.BeginPart(part++);
        list.RoundOutline(rect, radius, thickness, colour);
    }

    private float Width(ReadOnlySpan<char> words, float scale) => skin.Font.Measure(words, scale).X;

    /// <summary>
    /// How far small text next to big text has to come down for the two to stand on one line. Both are put in
    /// the middle of the same row, and the middle of a small letter is higher up its line than a big one's.
    /// </summary>
    private float Drop(float big, float little) => MathF.Round(skin.Font.LineHeight * (big - little) * 0.26f);

    /// <summary>
    /// Helper method to draw a number and what it is counted in after it, the number in its colour and the unit
    /// small and dim, both against the same side of an area.
    /// </summary>
    private void Value(UIDrawList list, UIRect area, Origin align, ReadOnlySpan<char> number, ReadOnlySpan<char> unit, float scale, Vector4 colour)
    {
        float gap = unit.Length > 0 ? 3.0f : 0.0f;
        var lowered = new UIRect(area.Min - new Vector2(0.0f, Drop(scale, small)), area.Max - new Vector2(0.0f, Drop(scale, small)));

        if (align == Origin.Right)
        {
            float unitWidth = Width(unit, small);
            Words(list, unit, lowered, Origin.Right, small, Dim);
            Words(list, number, new UIRect(area.Min, new Vector2(area.Max.X - unitWidth - gap, area.Max.Y)), Origin.Right, scale, colour);
            return;
        }

        float numberWidth = Width(number, scale);
        Words(list, number, area, Origin.Left, scale, colour);
        Words(list, unit, new UIRect(new Vector2(area.Min.X + numberWidth + gap, lowered.Min.Y), lowered.Max), Origin.Left, small, Dim);
    }

    /// <summary>Helper method to draw how full something is, a thin track with as much of it filled in.</summary>
    private void Meter(UIDrawList list, UIRect track, double share, Vector4 colour)
    {
        // Two parts whether there is anything in it or not, what comes after keeps its number either way
        int after = part + 2;
        Round(list, track, track.Height * 0.5f, WellColour with { W = 0.09f });

        float filled = MathF.Round(track.Width * (float)Math.Clamp(share, 0.0, 1.0));
        if (filled >= track.Height)
            Round(list, new UIRect(track.Min, new Vector2(track.Min.X + filled, track.Max.Y)), track.Height * 0.5f, colour);

        part = after;
    }

    /// <summary>Helper method to name a section on the left of its first line, with a word about it on the right.</summary>
    private void Title(UIDrawList list, float left, float top, string title, ReadOnlySpan<char> note)
    {
        var line = Box(left, top, COLUMN, TITLE - 6.0f);
        Words(list, title, line, Origin.Left, small, Plain);
        Words(list, note, line, Origin.Right, small, Dim);
    }

    /// <summary>Helper method to draw a number with what it is under it, see <see cref="CELL"/>.</summary>
    private void Cell(UIDrawList list, float left, float top, float width, ReadOnlySpan<char> label, double value, string format, string unit, Vector4 colour)
    {
        ink.Start().Add(value, format);
        Value(list, Box(left, top, width, 16.0f), Origin.Left, ink.Text, unit, text, colour);
        Words(list, label, Box(left, top - 16.0f, width, 14.0f), Origin.Left, small, Dim);
    }

    /// <summary>Helper method to draw what something is called and its number after it on one line.</summary>
    /// <returns>Where the next one can start.</returns>
    private float Inline(UIDrawList list, float left, float top, string label, double value, string format, string unit, Vector4 colour)
    {
        Words(list, label, Box(left, top, 120.0f, ROW), Origin.Left, small, Dim);
        left += Width(label, small) + 6.0f;

        ink.Start().Add(value, format);
        float width = Width(ink.Text, small) + (unit.Length > 0 ? Width(unit, small) + 3.0f : 0.0f);
        Value(list, Box(left, top, width + 2.0f, ROW), Origin.Left, ink.Text, unit, small, colour);

        return left + width + 16.0f;
    }

    /// <summary>The colour of how the frames are doing against what they are meant to do, sixty a second if nobody said.</summary>
    private Vector4 Health()
    {
        double wanted = Sample.FpsTarget > 0.0 ? Sample.FpsTarget : 60.0;
        return Sample.Fps >= wanted * 0.95 ? Good : Sample.Fps >= wanted * 0.5 ? Warn : Bad;
    }

    /// <summary>The colour of something that took so long against what it may take.</summary>
    private static Vector4 Against(double milliseconds, double budget, Vector4 fine) =>
        budget <= 0.0 || milliseconds <= budget ? fine : milliseconds <= budget * 2.0 ? Warn : Bad;
}

/// <summary>
/// A few characters to write numbers into before they are drawn, the same ones every time. What a
/// <see cref="PerformanceBoard"/> uses where anybody else would use a string.
/// </summary>
internal sealed class PerformanceInk
{
    private readonly char[] buffer = new char[96];
    private int length;

    /// <summary>What was written since <see cref="Start"/>. Only good until the next one.</summary>
    public ReadOnlySpan<char> Text => buffer.AsSpan(0, length);

    public PerformanceInk Start()
    {
        length = 0;
        return this;
    }

    public PerformanceInk Add(ReadOnlySpan<char> words)
    {
        int room = Math.Min(words.Length, buffer.Length - length);
        words[..room].CopyTo(buffer.AsSpan(length));
        length += room;
        return this;
    }

    /// <summary>Adds a number the way a format says ("0.00"), in the numbers of wherever the game is being played.</summary>
    public PerformanceInk Add(double value, ReadOnlySpan<char> format)
    {
        // Not a number is what a rate is before anything has come round, and nobody wants to read "NaN"
        if (!double.IsFinite(value)) value = 0.0;

        if (value.TryFormat(buffer.AsSpan(length), out int written, format))
            length += written;

        return this;
    }

    public PerformanceInk Add(long value)
    {
        if (value.TryFormat(buffer.AsSpan(length), out int written))
            length += written;

        return this;
    }
}
