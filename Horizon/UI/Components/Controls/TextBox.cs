using Horizon.Rendering;
using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A single line of text that can be edited. While it has the focus (it was clicked, or
/// <see cref="UIComponent.Focus"/> was called) whatever is typed on the keyboard goes into it,
/// and an <see cref="OnScreenKeyboard"/> or any other code can type into it with <see cref="Insert(char)"/>
/// and <see cref="Backspace"/>, focused or not.
/// <para>
/// It has a caret that typing happens at and a selection that typing replaces, and the keys that go with them.
/// The arrows, home and end move the caret (a word at a time with control, selecting with shift), backspace and
/// delete take a character (a word with control, and go on doing it while they are held), control with A selects
/// everything, and control with C, X and V copy, cut and paste. Clicking puts the caret where the click was,
/// dragging selects.
/// </para>
/// </summary>
public class TextBox : UIComponent
{
    private string style = "textbox";
    private string focusedStyle = "textbox_focused";

    /// <summary>
    /// The name of the skin region the box is drawn with, the one named after it with "_focused" on the end
    /// being what it wears while it is typed in, if the skin has that.
    /// </summary>
    public string Style
    {
        get => style;
        set
        {
            style = StyleStem(value, "_focused");
            focusedStyle = style + "_focused";
        }
    }

    private const float DEFAULT_WIDTH = 320.0f;
    private const float BLINK_INTERVAL = 0.5f;

    private IRuntimeValue? changedHandler;
    private IRuntimeValue? submittedHandler;
    private float blink;

    private string text = string.Empty;

    // Where typing happens, and where the selection started. Everything between the two is selected. Both count
    // characters from the start of the text
    private int caret, anchor;

    // How far the text is scrolled to the left to keep the caret in view, and where it was last drawn
    private float scroll;
    private float textLeft, textScale;
    private UISkin? paintedSkin;

    /// <summary>
    /// What is in the box. Setting it puts the caret at the end, as whatever the caret was in the middle of is gone.
    /// </summary>
    public string Text
    {
        get => text;
        set
        {
            value ??= string.Empty;
            if (value == text)
                return;

            text = value;
            caret = anchor = text.Length;
        }
    }

    /// <summary>Where the caret is, in characters from the start of the text.</summary>
    public int Caret => caret;

    /// <summary>What is selected, empty if nothing is.</summary>
    public string Selection => text[SelectionStart..SelectionEnd];

    private int SelectionStart => Math.Min(caret, anchor);
    private int SelectionEnd => Math.Max(caret, anchor);
    private bool HasSelection => caret != anchor;

    /// <summary>Shown, dimmed, while there is no text.</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>How many characters fit. Typing past it does nothing.</summary>
    public int MaxLength { get; set; } = 64;

    /// <summary>The only characters that can be typed, or null to allow everything the font can draw.</summary>
    public string? AllowedCharacters { get; set; }

    /// <summary>Called with the new text whenever typing changes it.</summary>
    public Action<string>? OnChanged { get; set; }

    /// <summary>Called with the text when enter is pressed, or <see cref="Submit"/> is called.</summary>
    public Action<string>? OnSubmitted { get; set; }

    /* A button in the box, for whatever fills it in some other way than typing. A file dialog, a list to pick
       from, a clear. It is part of the box and not a component of its own, so a box with one is laid out like a
       box without */

    private IRuntimeValue? buttonHandler;

    /// <summary>
    /// What the button in the box says, "..." for one that opens something to pick from. Empty, which is what it
    /// is unless set, for a box without a button. In a layout that is <c>button: "..."</c>.
    /// </summary>
    public string ButtonLabel { get; set; } = string.Empty;

    /// <summary>Which end of the box the button is at, the left unless told the right (<c>button_side: "right"</c>).</summary>
    public bool ButtonOnRight { get; set; }

    /// <summary>How wide the button is, 0 for as wide as the box is tall.</summary>
    public float ButtonWidth { get; set; }

    /// <summary>Called when the button is pressed, <c>on_button</c> in a layout.</summary>
    public Action? OnButtonPressed { get; set; }

    /// <summary>Whether the box has a button.</summary>
    public bool HasButton => ButtonLabel.Length > 0;

    /// <summary>Where the button is, as the box was last laid out. Empty for a box without one.</summary>
    public UIRect ButtonBounds
    {
        get
        {
            if (!HasButton || Bounds.IsEmpty)
                return default;

            float width = MathF.Min(ButtonWidth > 0.0f ? ButtonWidth : Bounds.Height, Bounds.Width * 0.5f);
            return ButtonOnRight
                ? new UIRect(new Vector2(Bounds.Max.X - width, Bounds.Min.Y), Bounds.Max)
                : new UIRect(Bounds.Min, new Vector2(Bounds.Min.X + width, Bounds.Max.Y));
        }
    }

    /// <summary>Presses the button of the box, the way a click on it does.</summary>
    public void PressButton()
    {
        if (!HasButton)
            return;

        OnButtonPressed?.Invoke();
        InvokeScript(buttonHandler);
    }

    // Whether the press that is going on started on the button, which is then not a press on the text
    private bool pressedButton;

    public TextBox()
    {
        Padding = new UIEdges(12.0f, 8.0f);
    }

    public TextBox(string text)
        : this()
    {
        Text = text;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Focusable => true;

    protected internal override bool Navigable => true;

    // Confirming on a text box starts typing into it
    protected internal override void OnActivate() => Focus();

    /// <summary>
    /// Types a character where the caret is (the end of the text, unless it was moved), if it is allowed and there
    /// is room. Whatever is selected is typed over.
    /// </summary>
    public bool Insert(char character)
    {
        if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.PrivateUse)
            return false;
        if (AllowedCharacters is not null && !AllowedCharacters.Contains(character))
            return false;
        if (text.Length - (SelectionEnd - SelectionStart) >= MaxLength)
            return false;

        int at = SelectionStart;
        text = string.Concat(text.AsSpan(0, at), new ReadOnlySpan<char>(in character), text.AsSpan(SelectionEnd));
        caret = anchor = at + 1;

        Changed();
        return true;
    }

    /// <summary>Types as much of a string as is allowed and fits.</summary>
    public void Insert(string text)
    {
        foreach (char character in text)
            Insert(character);
    }

    protected internal override void OnPaste(string text)
    {
        // One line of it, a text box has no use for the rest
        int end = text.AsSpan().IndexOfAny('\r', '\n');
        Insert(end < 0 ? text : text[..end]);
    }

    /// <summary>Removes what is selected, or failing that the character before the caret (the last one, unless the caret was moved).</summary>
    public void Backspace()
    {
        if (!HasSelection)
            anchor = Math.Max(0, caret - 1);

        RemoveSelection();
    }

    /// <summary>Removes what is selected, or failing that the character after the caret.</summary>
    public void Delete()
    {
        if (!HasSelection)
            anchor = Math.Min(text.Length, caret + 1);

        RemoveSelection();
    }

    /// <summary>Removes all of the text.</summary>
    public void Clear()
    {
        SelectAll();
        RemoveSelection();
    }

    /// <summary>Selects everything, with the caret at the end.</summary>
    public void SelectAll()
    {
        anchor = 0;
        caret = text.Length;
        blink = 0.0f;
    }

    /// <summary>Helper to take what is selected out of the text, which is a change only if anything was.</summary>
    private void RemoveSelection()
    {
        if (!HasSelection)
            return;

        int at = SelectionStart;
        text = text.Remove(at, SelectionEnd - at);
        caret = anchor = at;

        Changed();
    }

    /// <summary>Helper to put the caret somewhere, taking the selection along (shift is held) or dropping it.</summary>
    private void MoveCaret(int to, bool selecting)
    {
        caret = Math.Clamp(to, 0, text.Length);
        if (!selecting)
            anchor = caret;

        blink = 0.0f;
    }

    /// <summary>Helper to find where the word before or after a place starts. Across the spaces next to it, then across the word.</summary>
    private int WordFrom(int from, int direction)
    {
        int at = from;

        if (direction < 0)
        {
            while (at > 0 && char.IsWhiteSpace(text[at - 1])) at--;
            while (at > 0 && !char.IsWhiteSpace(text[at - 1])) at--;
        }
        else
        {
            while (at < text.Length && !char.IsWhiteSpace(text[at])) at++;
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        }

        return at;
    }

    /// <summary>Reports the text as entered, the way pressing enter does.</summary>
    public void Submit()
    {
        OnSubmitted?.Invoke(Text);
        InvokeScript(submittedHandler, new StringValue(Text));
    }

    private void Changed()
    {
        // The caret stays lit while typing.
        blink = 0.0f;

        TextChanged();

        OnChanged?.Invoke(Text);
        InvokeScript(changedHandler, new StringValue(Text));
    }

    /// <summary>Called whenever typing changes the text, for text boxes that make something of it.</summary>
    protected virtual void TextChanged()
    { }

    protected internal override void OnTextInput(char character)
    {
        switch (character)
        {
            case UIKeyboard.BACKSPACE:
                Backspace();
                break;
            case UIKeyboard.DELETE:
                Delete();
                break;
            case UIKeyboard.WORD_BACKSPACE:
                if (!HasSelection) anchor = WordFrom(caret, -1);
                RemoveSelection();
                break;
            case UIKeyboard.WORD_DELETE:
                if (!HasSelection) anchor = WordFrom(caret, 1);
                RemoveSelection();
                break;
            case UIKeyboard.ENTER:
                Submit();
                break;

            // The focus and what is open are the compositor's to deal with, see UICompositor.RouteKeyboard
            case UIKeyboard.TAB or UIKeyboard.SHIFT_TAB or UIKeyboard.ESCAPE or UIKeyboard.PASTE:
                break;

            case UIKeyboard.SELECT_ALL:
                SelectAll();
                break;
            case UIKeyboard.COPY:
                if (HasSelection) UIKeyboard.Copy(Selection);
                break;
            case UIKeyboard.CUT:
                if (HasSelection) UIKeyboard.Copy(Selection);
                RemoveSelection();
                break;

            // Without shift an arrow on a selection lands on the end of it the arrow points at
            case UIKeyboard.LEFT:
                MoveCaret(HasSelection ? SelectionStart : caret - 1, false);
                break;
            case UIKeyboard.RIGHT:
                MoveCaret(HasSelection ? SelectionEnd : caret + 1, false);
                break;
            case UIKeyboard.SELECT_LEFT:
                MoveCaret(caret - 1, true);
                break;
            case UIKeyboard.SELECT_RIGHT:
                MoveCaret(caret + 1, true);
                break;
            case UIKeyboard.WORD_LEFT or UIKeyboard.SELECT_WORD_LEFT:
                MoveCaret(WordFrom(caret, -1), character == UIKeyboard.SELECT_WORD_LEFT);
                break;
            case UIKeyboard.WORD_RIGHT or UIKeyboard.SELECT_WORD_RIGHT:
                MoveCaret(WordFrom(caret, 1), character == UIKeyboard.SELECT_WORD_RIGHT);
                break;
            case UIKeyboard.HOME or UIKeyboard.SELECT_HOME:
                MoveCaret(0, character == UIKeyboard.SELECT_HOME);
                break;
            case UIKeyboard.END or UIKeyboard.SELECT_END:
                MoveCaret(text.Length, character == UIKeyboard.SELECT_END);
                break;

            default:
                Insert(character);
                break;
        }
    }

    /// <summary>
    /// Helper to find which gap between two characters is nearest to a point, going by where the text was last drawn.
    /// </summary>
    private int CaretAt(Vector2 point)
    {
        if (paintedSkin is not { } skin || text.Length == 0)
            return 0;

        float wanted = point.X - textLeft;
        float before = 0.0f;

        for (int i = 1; i <= text.Length; i++)
        {
            float after = skin.Font.Measure(text.AsSpan(0, i), textScale, markup: false).X;

            // Nearer to the gap in front of this character than to the one behind it
            if (wanted < (before + after) * 0.5f)
                return i - 1;

            before = after;
        }

        return text.Length;
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        pressedButton = HasButton && ButtonBounds.Contains(point);
        if (pressedButton)
        {
            // The press was for the button and not for typing, the box doesn't keep the keyboard for it
            Unfocus();
            PressButton();
            return;
        }

        MoveCaret(CaretAt(point), false);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (!pressedButton)
            MoveCaret(CaretAt(point), true);
    }

    protected override void Update(float dt)
    {
        blink = (blink + dt) % (BLINK_INTERVAL * 2.0f);
    }

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        return new Vector2(DEFAULT_WIDTH, skin.Font.LineHeight * scale) + Padding.Total;
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        bool focused = enabled && IsFocused;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        // Art that isn't the colour the skin writes on says what to write on it in
        Vector4 ink = skin.TextColor;
        float rim = 0.0f;

        // A skin without art for it gets a flat box with an outline that lights up with the focus.
        if ((focused && skin.TryGetRegion(focusedStyle, out var art)) || skin.TryGetRegion(style, out art))
        {
            list.NineSlice(art, Bounds, tint);
            ink = art.TextOr(ink);

            // Art with a thick rim (a plank frame) has what is typed start inside of it, not on it
            rim = art.Border.Left * art.Scale + 3.0f;
        }
        else
        {
            list.Box(Bounds, skin.FieldColor * tint);
            if (enabled && IsHovered && !focused)
                list.Box(Bounds, skin.HoverColor * new Vector4(1.0f, 1.0f, 1.0f, 0.4f));

            // A thin rim that turns into a thick one in the colour of the skin while it is being typed in
            if (focused)
                list.Frame(Bounds, 2.0f, skin.HighlightColor);
            else
                list.Frame(Bounds, 1.0f, skin.BorderColor.W > 0.0f ? skin.BorderColor * tint : skin.HoverColor);
        }

        UIRect content = Bounds.Shrink(new UIEdges(MathF.Max(Padding.Left, rim), Padding.Top, MathF.Max(Padding.Right, rim), Padding.Bottom));
        bool empty = Text.Length == 0;

        // The button takes its end of the box, the text has the rest
        if (HasButton)
        {
            UIRect button = ButtonBounds;
            bool over = enabled && IsHovered && Module is { } module && button.Contains(module.ToLocal(module.Compositor.Pointer.Position));

            UIRect face = button.Shrink(new UIEdges(3.0f));
            list.Box(face, (over ? skin.HoverColor : skin.ControlColor with { W = skin.ControlColor.W * 0.35f }) * tint);
            list.Text(ButtonLabel, face, Origin.Center, scale, ink * tint, markup: false);

            content = ButtonOnRight
                ? new UIRect(content.Min, new Vector2(MathF.Min(content.Max.X, button.Min.X - 4.0f), content.Max.Y))
                : new UIRect(new Vector2(MathF.Max(content.Min.X, button.Max.X + 4.0f), content.Min.Y), content.Max);
        }

        // Text somebody typed is shown as typed, tags and all.
        float width = empty ? 0.0f : skin.Font.Measure(Text, scale, markup: false).X;
        float caretX = Offset(caret);

        // Text that has outgrown the box is scrolled just far enough to keep the caret, where the typing happens,
        // in view. One that isn't being typed in shows its end, which is where it will be typed in next
        if (!focused)
            scroll = MathF.Max(0.0f, width - content.Width);
        else if (caretX - scroll > content.Width - 2.0f)
            scroll = caretX - content.Width + 2.0f;
        else if (caretX < scroll)
            scroll = caretX;

        scroll = Math.Clamp(scroll, 0.0f, MathF.Max(0.0f, width - content.Width + 2.0f));

        UIRect line = new(content.Min - Vector2.UnitX * scroll, content.Max);

        // For finding the caret under the pointer later
        (paintedSkin, textLeft, textScale) = (skin, line.Min.X, scale);

        list.PushClip(content);

        if (focused && HasSelection)
        {
            float from = line.Min.X + Offset(SelectionStart), to = line.Min.X + Offset(SelectionEnd);
            list.Rect(new UIRect(new Vector2(from, content.Min.Y), new Vector2(to, content.Max.Y)), skin.HighlightColor with { W = 0.45f });
        }

        if (!empty)
            list.Text(Text, line, Origin.Left, scale, ink * tint, markup: false);
        else if (!focused)
            list.Text(Placeholder, line, Origin.Left, scale, ink * tint * new Vector4(1.0f, 1.0f, 1.0f, 0.45f));
        list.PopClip();

        if (focused && blink < BLINK_INTERVAL)
        {
            float x = Math.Clamp(line.Min.X + caretX, content.Min.X, content.Max.X);
            list.Rect(new UIRect(new Vector2(x, content.Min.Y), new Vector2(x + 2.0f, content.Max.Y)), ink);
        }

        if (!focused)
            PaintSelection(list);

        // How far into the text a gap between two characters is drawn
        float Offset(int index) => index <= 0 ? 0.0f : skin.Font.Measure(text.AsSpan(0, Math.Min(index, text.Length)), scale, markup: false).X;
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("placeholder", () => Placeholder, value => Placeholder = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("max_length", () => MaxLength, value => MaxLength = (int)value);
        Expose("style", () => Style, value => Style = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
        Expose("on_submitted", () => submittedHandler ?? new NullValue(), value => submittedHandler = value);
        Expose("button", () => ButtonLabel, value => ButtonLabel = value);
        Expose("button_side", () => ButtonOnRight ? "right" : "left", value => ButtonOnRight = value == "right");
        Expose("button_width", () => ButtonWidth, value => ButtonWidth = MathF.Max(0.0f, value));
        Expose("on_button", () => buttonHandler ?? new NullValue(), value => buttonHandler = value);
    }
}
