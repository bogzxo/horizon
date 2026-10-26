using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

/// <summary>
/// The heading of a section of a <see cref="SettingsPage"/>. On a page whose sections fold (<see cref="SettingsPage.CollapsibleSections"/>)
/// it has a chevron in front of it and a click on it folds the section's rows away or brings them back, the way the
/// sections of Visual Studio's properties window do. Anywhere else it's the plain heading it always was.
/// </summary>
internal sealed class SectionHeading(SettingsPage page, string group) : Label
{
    private static char right, down;

    public string Group { get; } = group;

    protected override bool HitTestVisible => page.CollapsibleSections;

    protected internal override void OnPointerDown(Vector2 point)
    {
        if (page.CollapsibleSections)
            page.ToggleSection(Group);
    }

    protected override void Update(float dt)
    {
        // Room in front of the words for the chevron, only on a page that folds
        float room = page.CollapsibleSections ? MathF.Max(16.0f, TextScale * 120.0f) : 0.0f;
        if (MathF.Abs(Padding.Left - room) > 0.5f)
            Padding = Padding with { Left = room };
        base.Update(dt);
    }

    protected override void Paint(UIDrawList list)
    {
        base.Paint(list);
        if (!page.CollapsibleSections)
            return;

        if (right == default)
        {
            UISymbols.TryGet("chevron-right", out right);
            UISymbols.TryGet("chevron-down", out down);
        }

        float size = MathF.Min(Padding.Left - 2.0f, Bounds.Height * 0.5f);
        var at = new Vector2(Bounds.Min.X + 1.0f, Bounds.Center.Y - size * 0.5f);
        Vector4 colour = list.Skin.DimTextColor;
        list.Symbol(page.IsFolded(Group) ? right : down, new UIRect(at, at + new Vector2(size, size)), colour);
    }
}
