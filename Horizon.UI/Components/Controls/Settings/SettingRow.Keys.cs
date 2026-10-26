using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// What sets a row, each offering the page the keys it's given before it does anything with them, so Up and Down go row
// to row from a toggle, a button, a dropdown that's shut and a slider alike (see SettingsPage.RowKey). A text box offers
// them through its TakeKey, these are the rest
internal sealed partial class SettingRow
{
    // A key offered to the page on behalf of one of its controls, true when the page took it
    private bool Offer(UIComponent control, char key) => page.RowKey(this, control, key);

    // Shift+Up and Shift+Down in a number's box, the number a step up or down between its least and its most and the
    // keyboard staying where it is. Up and Down alone go row to row
    private bool Stepped(NumberBox box, UISetting setting, char key)
    {
        if (key is not (UIKeyboard.SELECT_UP or UIKeyboard.SELECT_DOWN))
            return false;

        float step = MathF.Max(setting.Step, 0.01f) * (key == UIKeyboard.SELECT_UP ? 1.0f : -1.0f);
        float stepped = Kept(setting, box.Value + step);
        box.Value = stepped;
        if (box.IsFocused)
            box.Text = stepped.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        page.Changed(setting.Key, new NumberValue(stepped));
        return true;
    }

    private sealed class RowToggle(SettingRow row) : ToggleButton
    {
        protected internal override void OnTextInput(char character)
        {
            if (!row.Offer(this, character))
                base.OnTextInput(character);
        }
    }

    private sealed class RowButton(SettingRow row) : Button
    {
        protected internal override void OnTextInput(char character)
        {
            if (!row.Offer(this, character))
                base.OnTextInput(character);
        }
    }

    // Shut, Enter or Space opens it and Left and Right step through it, Up and Down are the page's. Open, the arrows are
    // its own to go down the list
    private sealed class RowDropdown(SettingRow row, string[] options) : Dropdown(options)
    {
        protected internal override void OnTextInput(char character)
        {
            if (!IsOpen && character is UIKeyboard.ENTER or ' ')
            {
                OnActivate();
                return;
            }

            if (!row.Offer(this, character))
                base.OnTextInput(character);
        }
    }

    // Tab gets to it and Left and Right move it a step, Up and Down still go row to row. Nothing but the pointer could
    // get at a slider before, and a row of one was passed over by the keyboard altogether
    private sealed class RowSlider(SettingRow row) : Slider
    {
        protected internal override bool TabStop => true;

        protected internal override void OnTextInput(char character)
        {
            if (row.Offer(this, character))
                return;

            if (character is UIKeyboard.LEFT or UIKeyboard.RIGHT)
                OnAdjust(character == UIKeyboard.RIGHT ? 1 : -1);
        }
    }

    // A row's name as wide as what it says up to as wide as the row has room for, so the reset after it sits beside the
    // name rather than off at the far end of the row. The room shrinks while a pill after the key takes some of it
    private sealed class RowTitle(float most) : Label
    {
        public float Most { get; set; } = most;

        protected override Vector2 Measure(UISkin skin)
        {
            Vector2 size = base.Measure(skin);
            return size with { X = MathF.Min(size.X, Most) };
        }
    }

    // A few small words in a pill after a row's key, what a search found it by or the page's note for it (set by the
    // other scope). The note's in the accent, it's about where the value comes from. A label puts its words against its
    // edges whatever its padding, so the pill is the words' width and a little either side, the words in the middle
    private sealed class RowPill(float most, bool accent) : Label
    {
        // How far the pill goes past its words either side, and how tall it is
        public const float PAD = 7.0f, HEIGHT = 20.0f;

        public float Most { get; } = most;

        protected override bool HitTestVisible => Tooltip.Length > 0;

        protected override Vector2 Measure(UISkin skin)
        {
            Vector2 size = base.Measure(skin);
            return new Vector2(MathF.Min(size.X + PAD * 2.0f, Most), HEIGHT);
        }

        protected override void Paint(UIDrawList list)
        {
            Vector4 tint = accent ? list.Skin.AccentColor : list.Skin.TextColor;
            list.RoundRect(Bounds, Bounds.Height * 0.5f, tint with { W = accent ? 0.14f : 0.07f });
            base.Paint(list);
        }
    }
}
