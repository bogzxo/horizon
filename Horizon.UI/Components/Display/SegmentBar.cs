using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One bar cut up by how much of a whole each of a few things is, each piece in a colour of its own. What a frame
/// cost pass by pass, what a disk is full of, how a vote went.
/// <code>
/// let passes = compositor.segments({ parent: card, size: vec(400, 8) });
/// </code>
/// <code>
/// bar.Clear();
/// bar.Add(1.2f, SegmentBar.Shade(0));          // so much, in the first of the colours it comes with
/// bar.Add(0.4f, new Vector4(1, 0.7f, 0.3f, 1));
/// bar.Whole = 2.0f;                             // out of this much, whatever is left over stays empty
/// </code>
/// <code>
/// bar.Marker = 7.0f;                            // a line across it that far along, in what the pieces are measured in
/// bar.AheadOpacity = 0.35f;                     // and what is past the line fainter, so it fills up as the line goes
/// </code>
/// A piece too thin to see is left out and the ones after it are where they would have been anyway. It makes no
/// garbage being fed or drawn.
/// </summary>
public class SegmentBar : UIComponent
{
    /// <summary>The most pieces it takes.</summary>
    public const int MAX_SEGMENTS = 32;

    private static readonly Vector2 DefaultSize = new(200.0f, 8.0f);

    // What things are told apart by when nobody says, eight that don't look like each other
    private static readonly Vector4[] Shades =
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

    private readonly float[] amounts = new float[MAX_SEGMENTS];
    private readonly Vector4[] colours = new Vector4[MAX_SEGMENTS];
    private readonly int[] names = new int[MAX_SEGMENTS];

    /// <summary>How many pieces it has.</summary>
    public int Count { get; private set; }

    /// <summary>What the whole bar stands for, 0 for everything in it added up.</summary>
    public float Whole { get; set; }

    /// <summary>The colour of the bar where nothing is, or null for the skin's text colour, nearly see-through.</summary>
    public Vector4? TrackColor { get; set; }

    /// <summary>
    /// How far along the bar a line is drawn across it, in what the pieces are measured in, below zero for no line.
    /// For a bar that is a stretch of time and something that is part of the way through it.
    /// </summary>
    public float Marker { get; set; } = -1.0f;

    /// <summary>The colour of the line, or null for the skin's text colour.</summary>
    public Vector4? MarkerColor { get; set; }

    /// <summary>
    /// How much of itself everything past the <see cref="Marker"/> is drawn with, 1 for no difference. Less and
    /// the bar is bright up to the line and faint after it.
    /// </summary>
    public float AheadOpacity { get; set; } = 1.0f;

    /// <summary>One of the eight colours it comes with, round and round, for telling things apart by number.</summary>
    public static Vector4 Shade(int index) => Shades[((index % Shades.Length) + Shades.Length) % Shades.Length];

    /// <summary>Takes every piece off it.</summary>
    public void Clear() => Count = 0;

    /// <summary>
    /// Puts a piece on the end of it.
    /// </summary>
    /// <param name="name">
    /// A number of its own that stays the same from one time to the next (which pass it is, say), so a piece keeps
    /// being that piece when the ones before it come and go. Left out it is its place along the bar.
    /// </param>
    public void Add(float amount, Vector4 colour, int name = -1)
    {
        if (Count >= MAX_SEGMENTS) return;

        amounts[Count] = MathF.Max(0.0f, amount);
        colours[Count] = colour;
        names[Count] = name >= 0 ? name : Count;
        Count++;
    }

    protected override Vector2 Measure(UISkin skin) => DefaultSize;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect bar = Bounds;

        list.BeginPart(0);
        list.RoundRect(bar, MathF.Min(3.0f, bar.Height * 0.5f), TrackColor ?? skin.TextColor with { W = 0.055f });

        float whole = Whole;
        if (whole <= 0.0f)
        {
            for (int i = 0; i < Count; i++) whole += amounts[i];
        }

        if (whole > 0.0f)
        {
            // Where the line is, in pixels along the bar. Without one everything counts as reached
            bool marked = Marker >= 0.0f;
            float line = marked ? MathF.Round(MathF.Min(1.0f, Marker / whole) * bar.Width) : bar.Width;
            bool split = marked && AheadOpacity < 1.0f;

            float along = 0.0f;
            for (int i = 0; i < Count; i++)
            {
                // A pixel of nothing between one and the next, and one too thin to see is left out
                float from = MathF.Round(along / whole * bar.Width);
                along += amounts[i];
                float to = MathF.Round(MathF.Min(1.0f, along / whole) * bar.Width) - 1.0f;

                if (to - from < 2.0f)
                    continue;

                if (!split)
                {
                    list.BeginPart(1 + names[i]);
                    list.RoundRect(new UIRect(new Vector2(bar.Min.X + from, bar.Min.Y), new Vector2(bar.Min.X + to, bar.Max.Y)), 2.0f, colours[i]);
                    continue;
                }

                // In two, what the line has been over and what it has yet to get to. Each is a part of its own
                // so the one growing and the other shrinking are blended as themselves between two ticks
                float reached = MathF.Min(to, line);
                if (reached - from >= 1.0f)
                {
                    list.BeginPart(1 + names[i]);
                    list.RoundRect(new UIRect(new Vector2(bar.Min.X + from, bar.Min.Y), new Vector2(bar.Min.X + reached, bar.Max.Y)), 2.0f, colours[i]);
                }

                float ahead = MathF.Max(from, line);
                if (to - ahead >= 1.0f)
                {
                    list.BeginPart(1 + MAX_SEGMENTS + names[i]);
                    list.RoundRect(
                        new UIRect(new Vector2(bar.Min.X + ahead, bar.Min.Y), new Vector2(bar.Min.X + to, bar.Max.Y)),
                        2.0f,
                        colours[i] with { W = colours[i].W * AheadOpacity });
                }
            }

            if (marked)
            {
                // A little taller than the bar, to be found by
                list.BeginPart(1 + MAX_SEGMENTS * 2);
                list.Rect(
                    new UIRect(new Vector2(bar.Min.X + line - 1.0f, bar.Min.Y - 3.0f), new Vector2(bar.Min.X + line + 1.0f, bar.Max.Y + 3.0f)),
                    MarkerColor ?? skin.TextColor);
            }
        }

        list.EndParts();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("whole", () => Whole, value => Whole = MathF.Max(0.0f, value));
        Expose("track_color", () => TrackColor ?? Vector4.Zero, value => TrackColor = value);
        Expose("marker", () => Marker, value => Marker = value);
        Expose("ahead_opacity", () => AheadOpacity, value => AheadOpacity = Math.Clamp(value, 0.0f, 1.0f));
    }
}
