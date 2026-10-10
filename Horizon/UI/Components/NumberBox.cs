using System.Globalization;
using System.Numerics;

using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// A text box for a number. Whatever is typed is read as one as it is typed, and while the box doesn't have the
/// focus it shows the number it holds, however it was written. Dragging across it changes the number without
/// the keyboard.
/// </summary>
public class NumberBox : TextBox
{
    private const string DIGITS = "0123456789.-";

    private IRuntimeValue? changedHandler;
    private float number;
    private float dragFrom;
    private float dragStart;
    private bool dragged;

    /// <summary>The number in the box.</summary>
    public float Value
    {
        get => number;
        set
        {
            number = value;

            // What somebody is in the middle of typing is theirs, "1." mustn't turn back into "1" under their fingers.
            if (!IsFocused)
                Text = Format(value);
        }
    }

    /// <summary>How much a unit of dragging across the box changes the number by.</summary>
    public float DragStep { get; set; } = 1.0f;

    /// <summary>Called with the new number whenever typing or dragging changes it.</summary>
    public Action<float>? OnValueChanged { get; set; }

    public NumberBox()
    {
        AllowedCharacters = DIGITS;
        MaxLength = 12;
        Text = "0";
    }

    public NumberBox(float value)
        : this()
    {
        Value = value;
    }

    protected override void TextChanged()
    {
        // Half a number ("-", "1.") isn't one yet, the last whole one stands until it is.
        if (!float.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float typed) || !float.IsFinite(typed))
            return;

        Report(typed);
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        base.OnPointerDown(point);

        dragFrom = point.X;
        dragStart = number;
        dragged = false;
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        float distance = point.X - dragFrom;

        // A little slack, or every click would nudge the number.
        if (!dragged && MathF.Abs(distance) < 4.0f)
            return;

        dragged = true;

        float moved = MathF.Round(dragStart + distance * DragStep * 0.25f, 3);
        if (moved == number)
            return;

        Text = Format(moved);
        Report(moved);
    }

    protected override void Update(float dt)
    {
        base.Update(dt);

        // Tidied up once whoever was typing has left, "007" and "" both stand for a number.
        if (!IsFocused && !IsPressed && Text != Format(number))
            Text = Format(number);
    }

    private void Report(float value)
    {
        if (value == number)
            return;

        number = value;
        OnValueChanged?.Invoke(value);
        InvokeScript(changedHandler, new NumberValue(value));
    }

    private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("value", () => Value, value => Value = value);
        Expose("drag_step", () => DragStep, value => DragStep = value);
        Expose("on_value_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
