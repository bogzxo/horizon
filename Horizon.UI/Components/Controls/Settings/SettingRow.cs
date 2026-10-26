using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

/// <summary>
/// A setting on a <see cref="SettingsPage"/>, its name with its key small and dim after it and a reset beside them while
/// it is changed, the sentence of what it does, and what sets it. On a page wide enough a toggle, a choice, a number or a
/// line of text sits in a column of its own down the right the way VS Code has them, and anything with buttons or a list
/// of choices goes on a line under the sentence. Lit under the pointer, a line in the accent down its left edge while it
/// is not what it comes as. A row that hangs off a switch (<see cref="UISetting.DependsOn"/>) is in a little and fades
/// while the switch is off. A right click has its reset and its key copied.
/// </summary>
internal sealed partial class SettingRow : StackPanel
{
    private const float PAD = 10.0f, TYPED_AFTER = 0.6f, BUTTON_COLUMN = 440.0f;

    // The column down the right what sets it goes in, the gap before it and the most the sentence on the left runs to.
    // Across the whole of a wide row a sentence was too long a line to read
    private const float CONTROL_COLUMN = 300.0f, COLUMN_GAP = 24.0f, HELP_MOST = 720.0f;

    // The least the left column can be before the row gives up on two columns and puts what sets it under the sentence
    private const float LEAST_TEXT = 300.0f;

    // How far in a row that hangs off another one is, and how faint its sentence and what sets it are while that's off.
    // At half it was near enough gone, and it can still be set
    private const float INDENT = 18.0f, DIMMED = 0.65f;

    // How wide a number's box is in the column down the right, its range and unit in front of it
    private const float NUMBER_WIDTH = 120.0f;

    // Those as big as the page is zoomed, a page made bigger on a big screen has its columns grow with its text
    private float ButtonColumn => BUTTON_COLUMN * page.ZoomNow;
    private float ControlColumn => CONTROL_COLUMN * page.ZoomNow;
    private float ColumnGap => COLUMN_GAP * page.ZoomNow;
    private float HelpMost => HELP_MOST * page.ZoomNow;
    private float LeastText => LEAST_TEXT * page.ZoomNow;
    private float NumberWidth => NUMBER_WIDTH * page.ZoomNow;

    // How faint its key is after its name
    private const float KEY_FAINT = 0.55f;

    // How round its corners are, the pointer's light, the keyboard's and the flash alike
    private const float ROUND = 6.0f;

    // How long the flash of a row gone to takes to fade, and how strong the accent is at its brightest
    private const float FLASH_TIME = 0.8f, FLASH_STRENGTH = 0.3f;

    private readonly SettingsPage page;
    private readonly Button reset;

    // Its name, its key after it and the sentence under them, rewritten with what a search found lit in them
    private readonly RowTitle name;
    private readonly Label key;
    private readonly Label? help;
    private string lit = string.Empty;

    // After its key, the keyword or choice a search found it by when that's nowhere it shows, and the note the page has
    // for it (set by the other scope), each a little pill and hidden while there's nothing to say
    private readonly RowPill foundBy;
    private readonly RowPill note;
    private string? foundByText, noteText;

    // What the top line has for the name once its key and reset have their room, the pills take theirs out of it
    private readonly float nameRoom;

    // How far into its flash it is, 1 as it's gone to and 0 once it's faded
    private float flash;
    private Action<IRuntimeValue?> show = _ => { };
    private bool modified;
    private string? typed;
    private float typedFor;

    // Where its name and sentence go and how wide they are, the row itself or its left column. The sentence stops at
    // HELP_MOST however wide the column, the room past it goes between the sentence and the control
    private readonly StackPanel text;
    private readonly float textWidth, helpWidth;

    // Whether what sets it is in the column down the right, rather than on a line under the sentence
    private readonly bool sideways;

    // How far in it is, 0 for a row that hangs off nothing
    private readonly float indent;

    // What fades while the switch it hangs off is off, its sentence and what sets it
    private readonly List<UIComponent> faded = [];
    private bool dimmed;

    public UISetting Setting { get; }

    protected override bool HitTestVisible => true;

    public SettingRow(SettingsPage page, UISetting setting, float width)
    {
        this.page = page;
        Setting = setting;
        Spacing = 4.0f;
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;

        // As wide as it was given whatever is in it, so nothing it shows or hides moves it about
        Size = new Vector2(width, 0.0f);
        indent = setting.DependsOn.Length > 0 ? INDENT : 0.0f;
        Padding = new UIEdges(PAD + 4.0f + indent, PAD, PAD, PAD);
        float full = width - PAD * 2.0f - 4.0f;
        float inner = full - indent;

        // Two columns when there's room and it's something that sits in one, the controls of every row lined up on the
        // row's right edge whether a row is in a little or not. The left column takes all the room the control's column
        // leaves, the sentence in it stops at HELP_MOST and the space past that is between the sentence and the control
        sideways = Sideways(setting) && full - ControlColumn - ColumnGap >= LeastText;
        if (sideways)
        {
            textWidth = inner - ControlColumn - ColumnGap;
            helpWidth = MathF.Min(HelpMost - indent, textWidth);
            var body = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = ColumnGap, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft });
            text = body.Add(new StackPanel { Spacing = 4.0f, Anchor = Origin.Top, Pivot = Origin.Top, Size = new Vector2(textWidth, 0.0f) });
            var column = body.Add(new StackPanel { Spacing = 4.0f, Anchor = Origin.Top, Pivot = Origin.Top, Size = new Vector2(ControlColumn, 0.0f) });

            // Against the column's right edge, so a toggle, a number and a dropdown all end where the row does
            line = column.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Right, Pivot = Origin.Right });
            faded.Add(column);
        }
        else
        {
            text = this;
            textWidth = inner;
            helpWidth = MathF.Min(HelpMost - indent, inner);
        }

        // Its name, its key small after it and its reset after those, the section is the heading over it (and over what
        // a search leaves). The key's always there, even when it reads the same as the name, it's what the file has
        var top = text.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        float side = MathF.Max(26.0f, page.RowTitleScale + 10.0f);
        float keyRoom = MathF.Min(textWidth * 0.4f, Room(setting.Key));
        nameRoom = textWidth - side - 8.0f - keyRoom - 8.0f;
        name = top.Add(new RowTitle(nameRoom) { Text = $"[b]{UIRichText.Literal(setting.Title)}[/b]", TextScale = page.RowTitleScale, Align = Origin.Left, Size = new Vector2(0.0f, page.RowControlHeight) });
        key = top.Add(new RowTitle(keyRoom) { Text = UIRichText.Literal(setting.Key), TextScale = page.RowBodyScale * 0.85f, Align = Origin.Left, Opacity = KEY_FAINT, Size = new Vector2(0.0f, page.RowControlHeight) });
        foundBy = top.Add(new RowPill(textWidth * 0.3f, accent: false) { TextScale = page.RowBodyScale * 0.85f, Align = Origin.Center, Visible = false });
        note = top.Add(new RowPill(textWidth * 0.3f, accent: true) { TextScale = page.RowBodyScale * 0.85f, Align = Origin.Center, Visible = false });

        // The glyph as big as the name, it was a speck beside it
        reset = top.Add(new RowButton(this)
        {
            Icon = "discard",
            Size = new Vector2(side, side),
            LabelScale = page.RowTitleScale,
            Style = "button_flat",
            Animated = false,
            Tooltip = page.Texts.ResetToDefault,
            OnPressed = () => page.ResetPressed(setting.Key)
        });

        if (setting.Help.Length > 0)
        {
            help = text.Add(new Label { Text = UIRichText.Literal(setting.Help), Wrap = true, TextScale = page.RowBodyScale, Align = Origin.TopLeft, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(helpWidth, 0.0f) });
            faded.Add(help);
        }

        // Under the sentence, what sets it and its buttons on one line, a slider with the number it's at beside it. A list
        // of choices makes lines of its own
        if (!sideways && (setting.Kind is not (UIFormKind.Flags or UIFormKind.Pairs) || setting.Buttons is { Length: > 0 }))
        {
            line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
            faded.Add(line);
        }

        // What sets it as wide as the row has left once its buttons have their room, a box with three buttons after it
        // ran off the end of the row and cut the last of them to its first two letters
        float[] widths = [.. (setting.Buttons ?? []).Select(label => MathF.Max(90.0f, label.Length * 8.5f + 28.0f))];
        float buttonsTake = widths.Sum() + 8.0f * widths.Length;

        // Rows with a few buttons keep them in a column of one width, so the first of them sits at the same x down the
        // page whether a row has two or three. Sized to the room it left they wandered by a few pixels row to row
        if (widths.Length > 1)
            buttonsTake = MathF.Max(buttonsTake, ButtonColumn);
        float room = sideways ? ControlColumn : setting.Kind == UIFormKind.Pairs ? MathF.Max(240.0f, helpWidth) : Math.Clamp(inner - buttonsTake, 160.0f, 420.0f);
        show = setting.ButtonsOnly ? _ => { } : MakeControl(setting, room);

        for (int i = 0; i < widths.Length; i++)
        {
            int which = i;
            string label = setting.Buttons![i];
            line!.Add(new RowButton(this)
            {
                Label = label,
                Size = new Vector2(widths[i], page.RowControlHeight),
                LabelScale = page.RowBodyScale,
                Animated = false,
                OnPressed = () => page.ButtonPressed(setting.Key, which)
            });
        }

        // A right click anywhere on it, its reset and its key or its line of the file copied, see SettingsPage.MenuOf
        OnContextMenu = point =>
        {
            if (Module is { } module && page.MenuOf(setting.Key) is { } menu)
                ContextMenu.Show(module, point, menu);
        };
    }

    // Whether a setting's control fits the column down the right, anything with buttons or a list of choices doesn't
    private static bool Sideways(UISetting setting) =>
        setting.Kind is UIFormKind.Flag or UIFormKind.Choice or UIFormKind.Number or UIFormKind.Whole or UIFormKind.Text
        && setting.Buttons is not { Length: > 0 } && !setting.ButtonsOnly;

    // About how wide a short text is written small after the name, the key and the pills, a little over so it isn't cut
    private float Room(string said) => said.Length * MathF.Max(7.0f, page.RowBodyScale * 0.5f) + 12.0f;

    /// <summary>Whether it was last shown changed, its accent line down the edge on.</summary>
    public bool Modified => modified;

    /// <summary>Whether it's in a little for hanging off another setting.</summary>
    public bool Indented => indent > 0.0f;

    /// <summary>Whether its sentence and what sets it are faint, the switch it hangs off being off. It can still be set.</summary>
    public bool Dimmed
    {
        get => dimmed;
        set
        {
            dimmed = value;
            foreach (UIComponent part in faded)
                part.Opacity = value ? DIMMED : 1.0f;
        }
    }

    /// <summary>The first thing of it the keyboard can go to that isn't its reset, what sets it, null for none.</summary>
    public UIComponent? FirstControl()
    {
        var found = new List<UIComponent>();
        UIModule.CollectFocusable(this, found);
        return found.Find(control => control != reset);
    }

    // Where what sets it goes, the column down the right or a line under the sentence, the row itself for a list of choices
    private StackPanel? line;

    private T Put<T>(T control) where T : UIComponent => line is not null ? line.Add(control) : Add(control);

    public void Show(IRuntimeValue? value, bool isModified)
    {
        modified = isModified;
        reset.Visible = isModified;

        // What the reset puts back said on it, only asked for while there's a reset to rest the pointer on
        if (isModified)
            reset.Tooltip = page.ResetText(Setting.Key);
        show(value);
        ShowNote(page.NoteOf?.Invoke(Setting.Key), page.NoteTipOf?.Invoke(Setting.Key));
    }

    /// <summary>What its note says after its key, null for none.</summary>
    public string? NoteText => noteText;

    /// <summary>What the pill after its key says a search found it by, plain, null for none.</summary>
    public string? FoundByText => foundByText is null ? null : UIRichText.Plain(foundByText);

    /// <summary>Where its note is, empty while it has none.</summary>
    public UIRect NoteBounds => note.Visible ? note.Bounds : default;

    // The page's note for it in its pill, its tooltip with it, hidden for none
    private void ShowNote(string? said, string? tip)
    {
        if (said == noteText && note.Tooltip == (tip ?? string.Empty))
            return;

        noteText = said;
        note.Text = said is null ? string.Empty : $"[color:accent]{UIRichText.Literal(said)}[/color]";
        note.Tooltip = tip ?? string.Empty;
        note.Visible = said is not null;
        FitName();
    }

    /// <summary>The keyword or choice a search found it by in its pill after the key, markup and all, null for none.</summary>
    public void ShowFoundBy(string? said)
    {
        if (said == foundByText)
            return;

        foundByText = said;
        foundBy.Text = said ?? string.Empty;
        foundBy.Visible = said is not null;
        FitName();
    }

    // The name given what the pills leave of its room, so a pill never pushes the reset past the left column
    private void FitName()
    {
        float taken = 0.0f;
        if (foundByText is not null)
            taken += MathF.Min(foundBy.Most, Room(UIRichText.Plain(foundByText)) + RowPill.PAD * 2.0f) + 8.0f;
        if (noteText is not null)
            taken += MathF.Min(note.Most, Room(noteText) + RowPill.PAD * 2.0f) + 8.0f;
        name.Most = MathF.Max(60.0f, nameRoom - taken);
    }

    /// <summary>What its name says now, markup and all, for a test to see what a search lit in it.</summary>
    public string TitleText => name.Text;

    /// <summary>Where its name's text is, as wide as what it says.</summary>
    public UIRect TitleBounds => name.Bounds;

    /// <summary>Where its reset is, empty while it's what it comes as and the reset is hidden.</summary>
    public UIRect ResetBounds => reset.Visible ? reset.Bounds : default;

    /// <summary>What its reset says when the pointer rests on it.</summary>
    public string ResetTooltip => reset.Tooltip;

    /// <summary>Where its sentence is, empty for a row with none.</summary>
    public UIRect HelpBounds => help?.Bounds ?? default;

    /// <summary>Where its key is after its name.</summary>
    public UIRect KeyBounds => key.Bounds;

    /// <summary>What its sentence says now, markup and all, empty for a row with none.</summary>
    public string HelpText => help?.Text ?? string.Empty;

    /// <summary>Its name, key and sentence written again with every word of a search lit in the accent, none for none lit.</summary>
    public void Highlight(string[] words)
    {
        // Rewritten only when the words change, a label lays its text out afresh every time it's given some
        string joined = string.Join('\n', words);
        if (joined == lit)
            return;

        lit = joined;
        name.Text = $"[b]{SettingsPage.Highlighted(Setting.Title, words)}[/b]";
        key.Text = SettingsPage.Highlighted(Setting.Key, words);
        if (help is not null)
            help.Text = SettingsPage.Highlighted(Setting.Help, words);
    }

    /// <summary>Whether it's still flashing from being gone to.</summary>
    public bool Flashing => flash > 0.001f;

    /// <summary>Its background flashed in the accent and faded back out, for a row gone to by a link to it.</summary>
    public void Flash()
    {
        flash = 1.0f;
        Tweens.Play(Tween.To(() => flash, value => flash = value, 0.0f, FLASH_TIME).SetEasing(Easing.InQuad), this);
    }

    protected override void Update(float dt)
    {
        if (typed is { } text)
        {
            typedFor += dt;
            if (typedFor >= TYPED_AFTER)
            {
                typed = null;
                page.Changed(Setting.Key, new StringValue(text));
            }
        }

        base.Update(dt);
    }

    protected override void Paint(UIDrawList list)
    {
        // always there, see-through when not lit, or the row's title and help shift along a quad as the pointer comes
        // by and get blended with each other between ticks (ToggleButton has why)
        list.RoundRect(Bounds, ROUND, list.Skin.TextColor with { W = IsHovered || HasHoveredChild() ? 0.04f : 0.0f });

        // A soft wash of the accent while the keyboard is somewhere in it, so the row being set stands out as a whole
        // and not only the ring round its control. There and see-through without it for the same reason
        Vector4 accent = list.Skin.AccentColor;
        list.RoundRect(Bounds, ROUND, accent with { W = HasFocusInside() ? 0.06f : 0.0f });

        // The flash of a row gone to over that, there and see-through the rest of the time for the same reason
        list.RoundRect(Bounds, ROUND, accent with { W = accent.W * FLASH_STRENGTH * flash });

        // The line down its edge while it's changed, focused or not, inside the row and in from its edge as far as the
        // row is, so marking it moves nothing. Always there and see-through while it's not, the same as the rest
        float edge = Bounds.Min.X + indent + 2.0f;
        list.Rect(new UIRect(new Vector2(edge, Bounds.Min.Y + 4.0f), new Vector2(edge + 3.0f, Bounds.Max.Y - 4.0f)), accent with { W = modified ? 1.0f : 0.0f });
        base.Paint(list);
    }

    // Whether whatever has the keyboard is this or something in it
    private bool HasFocusInside()
    {
        for (UIComponent? at = Module?.Compositor.Focus; at is not null; at = at.Parent)
        {
            if (at == this)
                return true;
        }

        return false;
    }

    // Anything in it under the pointer, however deep, its control is down in the right column
    private bool HasHoveredChild() => Hovered(this);

    private static bool Hovered(UIComponent of)
    {
        // By index, a foreach over the list would make an enumerator every paint
        IReadOnlyList<UIComponent> children = of.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].IsHovered || Hovered(children[i]))
                return true;
        }

        return false;
    }
}
