using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One out of a handful of options, shown between two arrows. Clicking its right half moves on to the next
/// option and its left half back to the one before, around and around.
/// For things there are only a few of. An anchor, a direction, a theme.
/// </summary>
public class Selector : UIComponent
{
    private string style = "button_flat";
    private string hoverStyle = "button_flat_hover";

    /// <summary>
    /// The name of the skin region this is drawn with, the one named after it with "_hover" on the end being
    /// what it wears under the pointer, if the skin has that. The names are put together here and not in the
    /// paint, which runs every tick and would make a string every time.
    /// </summary>
    public string Style
    {
        get => style;
        set
        {
            style = StyleStem(value, "_hover");
            hoverStyle = style + "_hover";
        }
    }
    private const float DEFAULT_WIDTH = 220.0f;

    private IRuntimeValue? changedHandler;
    private string[] options = [];
    private int index;

    /// <summary>What there is to choose from, in the order it is stepped through.</summary>
    public string[] Options
    {
        get => options;
        set
        {
            options = value ?? [];
            index = Math.Clamp(index, 0, Math.Max(0, options.Length - 1));
        }
    }

    /// <summary>Which of the options is chosen.</summary>
    public int Index
    {
        get => index;
        set => index = options.Length == 0 ? 0 : Math.Clamp(value, 0, options.Length - 1);
    }

    /// <summary>
    /// The chosen option itself, empty if there are none. Setting it to something that isn't an option
    /// changes nothing.
    /// </summary>
    public string Value
    {
        get => options.Length > 0 ? options[index] : string.Empty;
        set
        {
            int found = Array.IndexOf(options, value);
            if (found >= 0)
                index = found;
        }
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Called with the newly chosen option whenever a click changes it.</summary>
    public Action<string>? OnChanged { get; set; }

    public Selector()
    { }

    public Selector(params string[] options)
    {
        Options = options;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Navigable => true;

    protected internal override void OnActivate() => Step(1);

    protected internal override bool OnAdjust(int step)
    {
        Step(step);
        return true;
    }

    /// <summary>
    /// Moves on to the next option (1) or back to the one before (-1), around the ends, the way a click on either half
    /// does. For a gamepad, see <see cref="UINavigator.Adjust"/>.
    /// </summary>
    public void Step(int by)
    {
        if (options.Length < 2 || by == 0 || !EnabledInHierarchy)
            return;

        index = ((index + Math.Sign(by)) % options.Length + options.Length) % options.Length;

        OnChanged?.Invoke(Value);
        InvokeScript(changedHandler, new StringValue(Value));
    }

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        float height = skin.Font.LineHeight * scale + skin.ButtonPadding.Total.Y;

        return new Vector2(DEFAULT_WIDTH, skin.TryGetRegion(style, out var region) ? MathF.Max(height, region.Size.Y * 0.6f) : height);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        // Art that isn't the colour the skin writes on says what to write on it in
        Vector4 ink = skin.TextColor;

        bool lit = enabled && (IsHovered || IsSelected);

        // The body and the words as parts of their own, the hover box comes and goes and counted with the words it had
        // them blended with it for a frame between two ticks, a white flash (see UIDrawList.BeginPart)
        list.BeginPart(0);
        if ((lit && skin.TryGetRegion(hoverStyle, out var art)) || skin.TryGetRegion(style, out art))
        {
            list.NineSlice(art, Bounds, tint);
            ink = art.TextOr(ink);
        }
        else
        {
            list.Box(Bounds, skin.ControlColor * tint);
            if (lit)
                list.Box(Bounds, skin.HoverColor);
            if (skin.BorderColor.W > 0.0f)
                list.Frame(Bounds, 1.0f, skin.BorderColor * tint);
        }

        list.BeginPart(1);
        UIRect content = Bounds.Shrink(new UIEdges(10.0f, 0.0f));
        Vector4 arrows = skin.AccentColor * tint;

        list.Text("<", content, Origin.Left, scale, arrows, markup: false);
        list.Text(">", content, Origin.Right, scale, arrows, markup: false);
        list.Text(Localisation.Resolve(Value), content, Origin.Center, scale, ink * tint, markup: false);

        PaintSelection(list);
        list.EndParts();
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (!Bounds.Contains(point))
            return;

        // Left of the middle goes back, right of it goes on.
        Step(point.X < Bounds.Center.X ? -1 : 1);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // A list of texts, or one text with commas in it the way the old layouts wrote it
        Expose("options", () => options, value => Options = value);
        Expose("value", () => Value, value => Value = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("style", () => Style, value => Style = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
