using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>One card of <see cref="Notifications"/>, what it says and what can be done about it.</summary>
public sealed class UINotification
{
    /// <summary>What it says, plain text, wrapped to the card. Can change while it's up (work going on, then done).</summary>
    public string Text
    {
        get => text;
        set
        {
            if (text == value)
                return;
            text = value;
            WrappedFor = -1.0f;
        }
    }

    private string text = string.Empty;

    /// <summary>A line under the text, fainter, cut to the card: what work is doing right now. Empty for none.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// A bar along the card for work going on, how far through it is from 0 to 1, <see cref="float.NaN"/> for going on
    /// with no telling how far (the bar sweeps), null for no bar.
    /// </summary>
    public float? Progress { get; set; }

    /// <summary>The icon in front of it, a name of <see cref="UISymbols"/>.</summary>
    public string Icon { get; set; } = "info";

    /// <summary>The colour of the icon, null for the accent.</summary>
    public Vector4? IconColor { get; set; }

    /// <summary>How long it stays before it fades, in seconds, 0 for until it is closed.</summary>
    public float Seconds { get; set; } = 8.0f;

    /// <summary>What can be done about it, a button each along the bottom of the card. Pressing one closes the card too.</summary>
    public List<(string Label, Action Pressed)> Actions { get; } = [];

    /// <summary>Whether it was closed, by its x, an action, its time running out or the program.</summary>
    public bool Closed { get; private set; }

    /// <summary>Takes it away.</summary>
    public void Close() => Closed = true;

    /// <summary>Starts its time over (it changed, and wants reading again), and brings it back if it was fading.</summary>
    public void Renew()
    {
        Age = 0.0f;
        Lit = 0.0f;
        if (!Closed)
            Alpha = 1.0f;
    }

    // How long it has been up, how much of it is still there, and its text cut into lines for the width it was cut for
    internal float Age;
    internal float Alpha = 1.0f;

    // How far in it has come since it was shown, 0 to 1, at a steady rate (eased where it's used)
    internal float In;

    // How long ago it came or was renewed, in seconds, what its ring's two beats are timed by
    internal float Lit;

    // How far up from the bottom of the cards it's drawn, easing to where the cards under it put it, NaN until first placed
    internal float Place = float.NaN;
    internal readonly List<(int Start, int Length)> Lines = [];
    internal float WrappedFor = -1.0f;
    internal object? WrappedFont;
    internal float WrappedScale;
    internal readonly List<(string Label, string Display, UIRect Bounds)> Buttons = [];
    internal object? ButtonFont;
    internal float ButtonScale, ButtonWidth, ButtonAreaHeight;

    // Its detail as it was last cut to the card, for which detail, in what font, at what scale and in what width, cut
    // again only when one of them moved rather than every frame the card is up
    internal string? FittedDetailOf;
    internal object? FittedFont;
    internal float FittedScale, FittedWidth;
    internal string FittedDetail = string.Empty;
}

/// <summary>
/// Cards in a corner that say something and go away by themselves, VS Code's notifications. For what a status bar can't
/// hold, an error that wants reading, a file changed on disk, an extension that failed, with a button where there is
/// something to do about it. The newest at the bottom, the pointer over one holds it, its x closes it.
/// <code>
/// let toasts = compositor.notifications({ anchor: "bottom_right", pivot: "bottom_right", pos: vec(-12, 40) });
/// </code>
/// From code, <see cref="Show"/>. Put it last in its parent so it is drawn over the rest.
/// </summary>
public class Notifications : UIComponent
{
    // How far a card slides in from, in the UI's pixels
    private const float SLIDE = 36.0f;

    private const float PADDING = 12.0f, GAP = 8.0f, ICON = 16.0f, CLOSE = 14.0f, FADE = 0.4f, ARRIVE = 0.24f, ACTION_HEIGHT = 24.0f;

    // The bar of work going on, how thick, how wide the sweep of one that can't say how far, and how long a sweep takes
    private const float BAR = 4.0f, SWEEP = 0.3f, SWEEP_SECONDS = 1.4f;

    // The detail line, as big as the text is
    private const float DETAIL = 0.85f;

    // What has a card seen out of the corner of an eye. A bar down its left in its icon's colour for as long as it's up,
    // how thick and how far in, and a ring round it in the same colour that beats twice as it comes and is gone, how
    // thick and how long the two beats take. A card slid in quietly was read by whoever happened to be looking there
    private const float STRIPE = 3.0f, STRIPE_INSET = 4.0f, RING = 2.0f, CALL = 1.5f;

    // The clock of the sweep
    private float sweep;

    private readonly List<UINotification> shown = [];

    /// <summary>How wide a card is.</summary>
    public float Width { get; set; } = 380.0f;

    /// <summary>The most cards up at once, the oldest goes when another comes.</summary>
    public int MostShown { get; set; } = 4;

    /// <summary>The scale of the text, 0 for the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The cards that are up, the oldest first.</summary>
    public IReadOnlyList<UINotification> Cards => shown;

    protected override bool HitTestVisible => shown.Count > 0;

    /// <summary>
    /// Puts a card up and hands it back, to close it or to add to what can be done about it. Its text starts with a capital
    /// the way a sentence does, what a program says along a status bar in lower case came up on a card starting small.
    /// </summary>
    public UINotification Show(string text, string icon = "info", Vector4? iconColor = null, float seconds = 8.0f, params (string Label, Action Pressed)[] actions)
    {
        if (text.Length > 0 && char.IsLower(text[0]))
            text = char.ToUpperInvariant(text[0]) + text[1..];

        var card = new UINotification { Text = text, Icon = icon, IconColor = iconColor, Seconds = seconds };
        card.Actions.AddRange(actions);

        shown.Add(card);
        while (shown.Count > MostShown)
            shown.RemoveAt(0);

        return card;
    }

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float CardWidth => Parent?.Bounds.Width is > 0.0f and var available
        ? Math.Clamp(Width, 0.0f, MathF.Max(0.0f, available - MathF.Abs(Position.X)))
        : Width;

    private float HeightOf(UINotification card, UISkin skin)
    {
        float scale = ScaleOf(skin);
        float textWidth = MathF.Max(1.0f, CardWidth - PADDING * 3.0f - ICON - CLOSE);
        if (card.WrappedFor != textWidth || card.WrappedFont != skin.Font || card.WrappedScale != scale)
        {
            card.Lines.Clear();
            skin.Font.Wrap(card.Text, scale, textWidth, card.Lines, markup: false);
            card.WrappedFor = textWidth;
            card.WrappedFont = skin.Font;
            card.WrappedScale = scale;
        }

        float text = MathF.Max(1, card.Lines.Count) * skin.Font.LineHeight * scale;
        float detail = card.Detail.Length > 0 ? skin.Font.LineHeight * scale * DETAIL + GAP * 0.5f : 0.0f;
        float bar = card.Progress is not null ? BAR + GAP : 0.0f;
        LayoutActions(card, skin, CardWidth);
        return PADDING * 2.0f + MathF.Max(ICON, text) + detail + bar + card.ButtonAreaHeight;
    }

    protected override Vector2 Measure(UISkin skin)
    {
        float height = 0.0f;
        foreach (UINotification card in shown)
            height += HeightOf(card, skin) + GAP;

        return new Vector2(CardWidth, MathF.Max(0.0f, height - GAP));
    }

    // How quickly a card glides to its place, the share of the way left it goes in a second's worth of frames
    private const float GLIDE = 16.0f;

    protected override void Update(float dt)
    {
        sweep = (sweep + dt / SWEEP_SECONDS) % 1.0f;
        bool held = IsHovered;
        float call = MotionTime(CALL);
        bool calling = false;

        // Anything moving wants every frame: an idle window ticks four times a second, and a card sliding in, fading out,
        // making room or sweeping its bar went along in jumps
        bool moving = false;
        float soonest = float.MaxValue;
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            UINotification card = shown[i];

            // Its ring beating, which wants frames from a window that sleeps between keys
            if (card.Lit < call)
            {
                card.Lit += dt;
                calling = true;
            }

            // The pointer on the cards holds them all, somebody is reading
            if (!held && card.Seconds > 0.0f)
                card.Age += dt;

            // In from nothing as it arrives, as quick as the compositor's AnimationSpeed says, there at once with none
            float arrive = MotionTime(ARRIVE);
            card.In = arrive > 0.0f ? MathF.Min(1.0f, card.In + dt / arrive) : 1.0f;

            float fade = MotionTime(FADE);
            float going = card.Closed
                ? fade > 0.0f ? card.Alpha - dt / fade : 0.0f
                : card.Seconds > 0.0f ? Math.Clamp((card.Seconds - card.Age) / FADE, 0.0f, 1.0f) : 1.0f;
            card.Alpha = card.Closed ? going : MathF.Min(going, 1.0f - (1.0f - card.In) * (1.0f - card.In) * (1.0f - card.In));
            if (card.Alpha <= 0.0f)
            {
                shown.RemoveAt(i);
                moving = true;
                continue;
            }

            if (card.In < 1.0f || card.Closed || card.Alpha < 1.0f || card.Progress is float.NaN)
                moving = true;
            else if (card.Seconds > 0.0f && !held)
                soonest = MathF.Min(soonest, card.Seconds - FADE - card.Age);
        }

        // Each card eased to where the ones under it put it, so one coming or going has the rest glide rather than jump
        if (Module?.Compositor.Skin is { } skin)
        {
            float glide = 1.0f - MathF.Exp(-GLIDE * dt);
            float bottom = 0.0f;
            for (int i = shown.Count - 1; i >= 0; i--)
            {
                UINotification card = shown[i];
                if (float.IsNaN(card.Place) || MotionTime(ARRIVE) <= 0.0f)
                    card.Place = bottom;
                else
                    card.Place += (bottom - card.Place) * glide;
                if (MathF.Abs(card.Place - bottom) < 0.25f)
                    card.Place = bottom;
                else
                    moving = true;
                bottom += (HeightOf(card, skin) + GAP) * card.Alpha;
            }
        }

        var windows = GameEngine.Instance?.WindowManager;
        if (calling || moving)
            windows?.RequestFrames(0.1);
        // A tick on time for the fade to start from its first frame, an idle window's next one could be a quarter second late
        else if (soonest < float.MaxValue)
            windows?.RequestTickIn(Math.Max(0.0f, soonest));
    }

    /// <summary>Helper method for where each card is, the newest at the bottom of the bounds.</summary>
    private UIRect CardBounds(int index, UISkin skin)
    {
        float bottom = 0.0f;
        if (float.IsNaN(shown[index].Place))
        {
            for (int i = shown.Count - 1; i > index; i--)
                bottom += HeightOf(shown[i], skin) + GAP;
        }
        else
            bottom = MathF.Round(shown[index].Place);

        bottom += Bounds.Min.Y;
        return new UIRect(new Vector2(Bounds.Min.X, bottom), new Vector2(Bounds.Max.X, bottom + HeightOf(shown[index], skin)));
    }

    private static UIRect CloseBounds(UIRect card) =>
        new(new Vector2(card.Max.X - PADDING - CLOSE, card.Max.Y - PADDING - CLOSE), new Vector2(card.Max.X - PADDING, card.Max.Y - PADDING));

    // The primary action stays at the bottom right. Additional rows grow toward the text.
    private void LayoutActions(UINotification card, UISkin skin, float width)
    {
        float scale = ScaleOf(skin);
        if (card.ButtonWidth == width && card.ButtonFont == skin.Font && card.ButtonScale == scale && card.Buttons.Count == card.Actions.Count)
        {
            int i = 0;
            while (i < card.Actions.Count && card.Buttons[i].Label == card.Actions[i].Label)
                i++;
            if (i == card.Actions.Count)
                return;
        }

        card.Buttons.Clear();
        card.ButtonWidth = width;
        card.ButtonFont = skin.Font;
        card.ButtonScale = scale;
        float inset = MathF.Min(PADDING, width * 0.5f);
        float edge = width - inset;
        float right = edge;
        float bottom = PADDING;
        foreach (var (label, _) in card.Actions)
        {
            float natural = skin.Font.Measure(label, scale, markup: false).X + 2.0f * PADDING;
            float buttonWidth = MathF.Min(natural, edge - inset);
            if (right < edge && right - buttonWidth < inset)
            {
                right = edge;
                bottom += ACTION_HEIGHT + GAP;
            }
            var bounds = new UIRect(new Vector2(right - buttonWidth, bottom), new Vector2(right, bottom + ACTION_HEIGHT));
            string display = natural <= buttonWidth ? label : Fitted(label, skin, scale, buttonWidth - 2.0f * PADDING);
            card.Buttons.Add((label, display, bounds));
            right -= buttonWidth + GAP;
        }
        card.ButtonAreaHeight = card.Actions.Count == 0 ? 0.0f : bottom - PADDING + ACTION_HEIGHT + GAP;
    }

    private static UIRect ActionBounds(UIRect bounds, UINotification card, int action)
    {
        var button = card.Buttons[action].Bounds;
        return new UIRect(bounds.Min + button.Min, bounds.Min + button.Max);
    }

    /// <summary>
    /// Where a card that's up is, empty for one that's gone or going, for what lists what's on screen to point at it
    /// (Hex's agents found nothing that said Use Visual Studio Keys on its card).
    /// </summary>
    public UIRect CardBoundsOf(UINotification card) =>
        shown.IndexOf(card) is var index and >= 0 && !card.Closed && Module?.Compositor.Skin is { } skin ? CardBounds(index, skin) : default;

    /// <summary>Where a button of a card that's up is, empty for one that's gone or a button it hasn't got. See <see cref="CardBoundsOf"/>.</summary>
    public UIRect ActionBoundsOf(UINotification card, int action) =>
        action >= 0 && action < card.Actions.Count && CardBoundsOf(card) is { Width: > 0.0f } bounds
            ? ActionBounds(bounds, card, action) : default;

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (Module?.Compositor.Skin is not { } skin)
            return;

        for (int i = 0; i < shown.Count; i++)
        {
            UINotification card = shown[i];
            UIRect bounds = CardBounds(i, skin);
            if (!bounds.Contains(point) || card.Closed)
                continue;

            if (CloseBounds(bounds).Contains(point))
            {
                card.Close();
                return;
            }

            for (int action = 0; action < card.Actions.Count; action++)
            {
                if (ActionBounds(bounds, card, action).Contains(point))
                {
                    card.Close();
                    card.Actions[action].Pressed();
                    return;
                }
            }
        }
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = ScaleOf(skin), line = skin.Font.LineHeight * scale;
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

        for (int i = 0; i < shown.Count; i++)
        {
            UINotification card = shown[i];
            UIRect bounds = CardBounds(i, skin);
            Vector4 fade = new(1.0f, 1.0f, 1.0f, card.Alpha);

            list.BeginPart(i);

            // In from the right as it fades in, back out that way as it fades out
            list.PushVisual(new Vector2((1.0f - card.Alpha) * SLIDE, 0.0f), Vector2.One, bounds.Center, 1.0f);

            // On what every popup sits on, faded with the card
            list.PushVisual(Vector2.Zero, Vector2.One, bounds.Center, card.Alpha);
            list.Popup(bounds, Module?.Compositor.PopupGlass);
            list.PopVisual();

            // The bar down its left, in the colour its icon is (the accent, or an error's red), clear of the round corners
            Vector4 mark = card.IconColor ?? skin.AccentColor;
            float corner = MathF.Min(skin.PopupRadius, bounds.Height * 0.5f) * 0.75f;
            list.RoundRect(new UIRect(new Vector2(bounds.Min.X + STRIPE_INSET, bounds.Min.Y + corner), new Vector2(bounds.Min.X + STRIPE_INSET + STRIPE, bounds.Max.Y - corner)),
                STRIPE * 0.5f, mark * fade);

            // And the ring, bright as it comes, down, up half as bright and gone. Not at all in a UI told not to move
            float call = MotionTime(CALL);
            if (!card.Closed && card.Lit < call)
            {
                float through = card.Lit / call;
                float beat = (1.0f - through) * (0.5f + 0.5f * MathF.Cos(through * MathF.PI * 4.0f));
                list.RoundOutline(bounds, skin.PopupRadius, RING, mark with { W = mark.W * beat * card.Alpha });
            }

            float top = bounds.Max.Y - PADDING;
            float left = bounds.Min.X + PADDING;
            list.Symbol(card.Icon, new UIRect(new Vector2(left, top - ICON), new Vector2(left + ICON, top)), (card.IconColor ?? skin.AccentColor) * fade);

            UIRect close = CloseBounds(bounds);
            list.Symbol("close", close, skin.TextColor * new Vector4(1.0f, 1.0f, 1.0f, (close.Contains(pointer) ? 0.9f : 0.5f) * card.Alpha));

            float textLeft = left + ICON + PADDING;
            for (int l = 0; l < card.Lines.Count; l++)
            {
                var (start, length) = card.Lines[l];
                list.Text(card.Text.AsSpan(start, length), new Vector2(textLeft, MathF.Round(top - l * line)), scale, skin.TextColor * fade, markup: false);
            }

            // What the work is doing, a fainter line under the text, cut to the card
            float below = top - MathF.Max(1, card.Lines.Count) * line - GAP * 0.5f;
            float right = close.Min.X - PADDING;
            if (card.Detail.Length > 0)
            {
                float detailScale = scale * DETAIL;
                list.Text(FittedDetail(card, skin, detailScale, right - textLeft), new Vector2(textLeft, MathF.Round(below)), detailScale,
                    skin.TextColor * new Vector4(1.0f, 1.0f, 1.0f, 0.6f * card.Alpha), markup: false);
                below -= line * DETAIL + GAP * 0.5f;
            }

            // The bar, filled as far as the work got, or a stretch of it sweeping along when that can't be said
            if (card.Progress is { } progress)
            {
                var track = new UIRect(new Vector2(textLeft, below - BAR - GAP * 0.5f), new Vector2(bounds.Max.X - PADDING, below - GAP * 0.5f));
                list.RoundRect(track, BAR * 0.5f, skin.TextColor * new Vector4(1.0f, 1.0f, 1.0f, 0.12f * card.Alpha));
                float from = float.IsNaN(progress) ? MathF.Max(0.0f, sweep * (1.0f + SWEEP) - SWEEP) : 0.0f;
                float to = float.IsNaN(progress) ? MathF.Min(1.0f, sweep * (1.0f + SWEEP)) : Math.Clamp(progress, 0.0f, 1.0f);
                if (to > from)
                {
                    var fill = new UIRect(new Vector2(track.Min.X + track.Width * from, track.Min.Y), new Vector2(track.Min.X + track.Width * to, track.Max.Y));
                    list.RoundRect(fill, BAR * 0.5f, skin.AccentColor * fade);
                }
            }

            for (int action = 0; action < card.Actions.Count; action++)
            {
                UIRect button = ActionBounds(bounds, card, action);
                bool lit = button.Contains(pointer);

                // Only the first in the accent, the rest a quiet fill of the text's colour. Three gold buttons had none of
                // them the one to press
                if (action == 0)
                {
                    list.RoundRect(button, skin.CornerRadius, (lit ? skin.AccentColor : skin.AccentColor with { W = skin.AccentColor.W * 0.75f }) * fade);
                    list.Text(card.Buttons[action].Display, button, Origin.Center, scale, skin.BarTextColor * fade, markup: false);
                }
                else
                {
                    list.RoundRect(button, skin.CornerRadius, skin.TextColor with { W = (lit ? 0.18f : 0.1f) * card.Alpha });
                    list.Text(card.Buttons[action].Display, button, Origin.Center, scale, skin.TextColor * fade, markup: false);
                }
            }

            list.PopVisual();
        }

        list.EndParts();
    }

    // A card's detail cut to its width, kept on the card until the detail, the font, the scale or the width changes
    private static string FittedDetail(UINotification card, UISkin skin, float scale, float width)
    {
        if (!ReferenceEquals(card.FittedDetailOf, card.Detail) || !ReferenceEquals(card.FittedFont, skin.Font) || card.FittedScale != scale || card.FittedWidth != width)
            (card.FittedDetailOf, card.FittedFont, card.FittedScale, card.FittedWidth, card.FittedDetail) = (card.Detail, skin.Font, scale, width, Fitted(card.Detail, skin, scale, width));
        return card.FittedDetail;
    }

    // A line cut to a width with an ellipsis, the whole of it when it fits
    private static string Fitted(string line, UISkin skin, float scale, float width)
    {
        if (width <= 0.0f)
            return string.Empty;
        if (skin.Font.Measure(line, scale, markup: false).X <= width)
            return line;

        if (skin.Font.Measure("\u2026", scale, markup: false).X > width)
            return string.Empty;
        int keep = line.Length;
        char[]? rented = null;
        int needed = line.Length + 1;
        Span<char> buffer = needed <= 256 ? stackalloc char[needed] : rented = System.Buffers.ArrayPool<char>.Shared.Rent(needed);
        try
        {
            line.CopyTo(buffer);
            while (keep > 0)
            {
                buffer[keep] = '\u2026';
                if (skin.Font.Measure(buffer[..(keep + 1)], scale, markup: false).X <= width)
                    break;
                keep = keep * 3 / 4;
            }
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
        return string.Concat(line.AsSpan(0, keep), "\u2026");
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("width", () => Width, value => Width = value);
        Expose("most_shown", () => MostShown, value => MostShown = Math.Max(1, (int)value));
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("show", () => new NativeFunctionValue((args, _) =>
        {
            Show(args.Length > 0 && args[0] is StringValue text ? text.Value : string.Empty, args.Length > 1 && args[1] is StringValue icon ? icon.Value : "info");
            return Values.Null;
        }), _ => { });
    }
}
