using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A piece of text. Sizes itself to fit unless told otherwise, lines are split on '\n'.
/// Told to <see cref="Wrap"/>, text that is wider than the label is broken between words into as many lines as
/// it takes, and the label is as tall as those. That wants a width to wrap to, a <see cref="UIComponent.Size"/>
/// with a width, or a <see cref="UIComponent.Fill"/> across. A label with neither is one line, wrap or no wrap.
/// <code>
/// let about = compositor.label({ size: vec(400, 0), wrap: true, align: "top_left", text: "..." });
/// </code>
/// Tags in square brackets style the text, <c>[b]bold[/b]</c>, <c>[i]italic[/i]</c>, <c>[color:#ff8040]</c> and
/// <c>[size:1.5]</c> with <c>[/color]</c> and <c>[/size]</c> to go back, <c>[reset]</c>, and the rest that
/// <see cref="UIRichText"/> lists. Told it is <see cref="Markdown"/> it reads that as well, headings, lists, quotes,
/// code and links (<see cref="OnLink"/> says when one is clicked).
/// </summary>
public class Label : UIComponent
{
    public string Text { get; set; } = string.Empty;

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The colour of the text, or null to use the skin's.</summary>
    public Vector4? Color { get; set; }

    /// <summary>Where the text goes when the label is bigger than it.</summary>
    public Origin Align { get; set; } = Origin.Center;

    /// <summary>
    /// Whether text that is wider than the label is broken into more lines rather than running off the edge.
    /// The label grows downwards for them unless it has a height of its own. See the class for what it wraps to.
    /// </summary>
    public bool Wrap { get; set; }

    /// <summary>
    /// Whether the text is markdown, **bold**, *italic*, `code`, # headings, - lists, &gt; quotes, --- rules, links.
    /// Off unless asked, a * or a # in plain text is just that. The tags in square brackets work either way.
    /// </summary>
    public bool Markdown { get; set; }

    /// <summary>Called with where a link of the markdown goes when it is clicked, <c>[the words](where)</c>.</summary>
    public Action<string>? OnLink { get; set; }

    // The text laid out with its styles, for text that has any (see UsesRichText), and which text that was decided for
    private readonly UIRichText rich = new();
    private string? richDecidedFor;
    private bool hasTags;

    // Whether the text was laid out with its styles the last time it was measured
    private bool drawnRich;

    // The lines the text was last cut into, and what it was cut for, so it is only cut again when any of that changes
    private readonly List<(int Start, int Length)> lines = [];
    private string? wrappedText;
    private float wrappedScale, wrappedWidth;

    // Whether the lines are what the text is drawn as right now
    private bool wrapped;

    public Label()
    { }

    public Label(string text)
    {
        Text = text;
    }

    /// <summary>How many lines the text is drawn as, once it has been laid out.</summary>
    public int LineCount => drawnRich ? rich.LineCount : wrapped ? lines.Count : 1 + Text.AsSpan().Count('\n');

    /// <summary>
    /// Helper method to say whether text is laid out with its styles, which is markdown or a style tag in it. Plain
    /// text goes the way it always went. Decided once for every text it is given, not every tick.
    /// </summary>
    private bool UsesRichText(string text)
    {
        if (!ReferenceEquals(text, richDecidedFor))
        {
            richDecidedFor = text;
            hasTags = UIRichText.HasTags(text);
        }

        return Markdown || hasTags;
    }

    // A label with a link in it takes the pointer, for the link
    protected override bool HitTestVisible => drawnRich && OnLink is not null && rich.HasLinks;

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (OnLink is null || !drawnRich || !Bounds.Contains(point) || Module?.Compositor.Skin is null)
            return;

        ReadOnlySpan<char> link = rich.LinkAt(point, Bounds, Align);
        if (!link.IsEmpty)
            OnLink(link.ToString());
    }

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    /// <summary>
    /// Helper method to work out how wide there is to wrap to. The label's own width if it has one, or the width it was
    /// given last time if it fills across (which is a step behind, and catches up on the next layout). Nothing otherwise.
    /// </summary>
    private float WrapWidth()
    {
        if (Size.X > 0.0f)
            return Size.X;

        return (Fill & UIFill.Horizontal) != 0 ? Bounds.Width : 0.0f;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = ScaleOf(skin);
        float width = Wrap ? WrapWidth() : 0.0f;

        // What it says, a key in a layout being the text of it in the language that is loaded, see Localisation
        string text = Resolve(Text);

        // Styled text lays itself out, and is as wide as it was told to wrap to when it was
        drawnRich = UsesRichText(text);
        if (drawnRich)
        {
            rich.Build(text, skin.Font, scale, width, Markdown);
            wrapped = false;
            return new Vector2(width > 0.0f ? width : rich.Width, rich.Height);
        }

        wrapped = width > 0.0f;
        if (!wrapped)
            return skin.Font.Measure(text, scale);

        // Only cut again when the text, its size or the room has changed. The text is a string, the same one is the same text
        if (!ReferenceEquals(wrappedText, text) || wrappedScale != scale || wrappedWidth != width)
        {
            skin.Font.Wrap(text, scale, width, lines);
            (wrappedText, wrappedScale, wrappedWidth) = (text, scale, width);
        }

        // As wide as it was told to be, which is what it was wrapped to
        return new Vector2(width, skin.Font.Measure(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), scale).Y);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector4 color = Color ?? skin.TextColor;
        string text = Resolve(Text);

        if (drawnRich)
        {
            rich.Paint(list, Bounds, Align, color);
            return;
        }

        if (wrapped)
            list.Text(text, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines), Bounds, Align, ScaleOf(skin), color);
        else
            list.Text(text, Bounds, Align, ScaleOf(skin), color);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("wrap", () => Wrap, value => Wrap = value);
        Expose("markdown", () => Markdown, value => Markdown = value);
        Expose(
            "align",
            () => UIScript.FromEnum(Align),
            value => Align = UIScript.ToEnum<Origin>(value, "align"));
    }
}
