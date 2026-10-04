using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// A single line of text that can be edited. While it has the focus (it was clicked, or
/// <see cref="UIComponent.Focus"/> was called) whatever is typed on the keyboard goes into it;
/// an <see cref="OnScreenKeyboard"/> or any other code can type into it with <see cref="Insert(char)"/>
/// and <see cref="Backspace"/>, focused or not.
/// </summary>
public class TextBox : UIComponent
{
    private const float DEFAULT_WIDTH = 320.0f;
    private const float BLINK_INTERVAL = 0.5f;

    private IRuntimeValue? changedHandler;
    private IRuntimeValue? submittedHandler;
    private float blink;

    public string Text { get; set; } = string.Empty;

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

    /// <summary>Types a character at the end of the text, if it is allowed and there is room.</summary>
    public bool Insert(char character)
    {
        if (char.IsControl(character) || Text.Length >= MaxLength)
            return false;
        if (AllowedCharacters is not null && !AllowedCharacters.Contains(character))
            return false;

        Text += character;
        Changed();
        return true;
    }

    /// <summary>Types as much of a string as is allowed and fits.</summary>
    public void Insert(string text)
    {
        foreach (char character in text)
            Insert(character);
    }

    /// <summary>Removes the last character.</summary>
    public void Backspace()
    {
        if (Text.Length == 0)
            return;

        Text = Text[..^1];
        Changed();
    }

    /// <summary>Removes all of the text.</summary>
    public void Clear()
    {
        if (Text.Length == 0)
            return;

        Text = string.Empty;
        Changed();
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

        OnChanged?.Invoke(Text);
        InvokeScript(changedHandler, new StringValue(Text));
    }

    protected internal override void OnTextInput(char character)
    {
        if (character == UIKeyboard.BACKSPACE)
            Backspace();
        else if (character == UIKeyboard.ENTER)
            Submit();
        else
            Insert(character);
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

        list.Rect(Bounds, skin.TrackColor * tint);
        if (enabled && IsHovered && !focused)
            list.Rect(Bounds, skin.HoverColor * new Vector4(1.0f, 1.0f, 1.0f, 0.4f));
        list.Outline(Bounds, 2.0f, focused ? skin.HighlightColor : skin.HoverColor);

        UIRect content = Bounds.Shrink(Padding);
        bool empty = Text.Length == 0;

        // Text that has outgrown the box keeps its end, where the typing happens, in view.
        float width = empty ? 0.0f : skin.Font.Measure(Text, scale).X;
        float overflow = MathF.Max(0.0f, width - content.Width);
        UIRect line = new(content.Min - Vector2.UnitX * overflow, content.Max);

        list.PushClip(content);
        if (!empty)
            list.Text(Text, line, Origin.Left, scale, skin.TextColor * tint);
        else if (!focused)
            list.Text(Placeholder, line, Origin.Left, scale, skin.TextColor * tint * new Vector4(1.0f, 1.0f, 1.0f, 0.45f));
        list.PopClip();

        if (focused && blink < BLINK_INTERVAL)
        {
            float x = MathF.Min(line.Min.X + width + 2.0f, content.Max.X);
            list.Rect(new UIRect(new Vector2(x, content.Min.Y), new Vector2(x + 2.0f, content.Max.Y)), skin.TextColor);
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text", () => Text, value => Text = value);
        Expose("placeholder", () => Placeholder, value => Placeholder = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("max_length", () => MaxLength, value => MaxLength = (int)value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
        Expose("on_submitted", () => submittedHandler ?? new NullValue(), value => submittedHandler = value);
    }
}
