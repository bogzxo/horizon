using System.Numerics;

using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

public enum UIDirection
{
    Vertical,
    Horizontal
}

/// <summary>
/// A panel that lines its children up one after another. Top to bottom, or left to right.
/// Across the line a child is placed by its <see cref="UIComponent.Anchor"/> (to one side or centred),
/// or stretched to the full width of the line if it fills that axis or the stack is told to
/// <see cref="Stretch"/> everything.
/// </summary>
public class StackPanel : Panel
{
    public UIDirection Direction { get; set; } = UIDirection.Vertical;

    /// <summary>The gap between two children, or a negative number to use the skin's.</summary>
    public float Spacing { get; set; } = -1.0f;

    /// <summary>Makes every child as wide as a vertical stack, or as tall as a horizontal one.</summary>
    public bool Stretch { get; set; }

    // The spacing the last measure settled on, for arrange to use.
    private float gap;

    protected override Vector2 Measure(UISkin skin)
    {
        gap = Spacing >= 0.0f ? Spacing : skin.Spacing;

        bool vertical = Direction == UIDirection.Vertical;
        float along = 0.0f;
        float across = 0.0f;
        int count = 0;

        foreach (var child in ChildSpan)
        {
            if (!child.Visible)
                continue;

            Vector2 size = child.DesiredSize;
            along += vertical ? size.Y : size.X;
            across = MathF.Max(across, vertical ? size.X : size.Y);
            count++;
        }

        along += gap * MathF.Max(0, count - 1);

        return (vertical ? new Vector2(across, along) : new Vector2(along, across)) + Padding.Total;
    }

    protected override void Arrange(UIRect content)
    {
        bool vertical = Direction == UIDirection.Vertical;

        // The edge the next child starts from. The top of a vertical stack, the left of a horizontal one.
        float cursor = vertical ? content.Max.Y : content.Min.X;

        foreach (var child in ChildSpan)
        {
            if (!child.Visible)
                continue;

            Vector2 size = child.DesiredSize;
            Vector2 side = child.Anchor.ToVector();

            if (vertical)
            {
                if (Stretch || (child.Fill & UIFill.Horizontal) != 0)
                {
                    size.X = content.Width;
                    side.X = 0.0f;
                }

                // Slides the child between the left edge (-0.5) and the right edge (0.5) of the stack.
                float centerX = content.Center.X + side.X * (content.Width - size.X);
                child.ArrangeTree(UIRect.FromCenter(new Vector2(centerX, cursor - size.Y * 0.5f), size));

                cursor -= size.Y + gap;
            }
            else
            {
                if (Stretch || (child.Fill & UIFill.Vertical) != 0)
                {
                    size.Y = content.Height;
                    side.Y = 0.0f;
                }

                float centerY = content.Center.Y + side.Y * (content.Height - size.Y);
                child.ArrangeTree(UIRect.FromCenter(new Vector2(cursor + size.X * 0.5f, centerY), size));

                cursor += size.X + gap;
            }
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("spacing", () => Spacing, value => Spacing = value);
        Expose("stretch", () => Stretch, value => Stretch = value);
        Expose(
            "direction",
            () => UIScript.FromEnum(Direction),
            value => Direction = UIScript.ToEnum<UIDirection>(value, "direction"));
    }
}
