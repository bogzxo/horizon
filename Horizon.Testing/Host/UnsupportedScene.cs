using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Testing;

/// <summary>
/// What an example that can't run on this platform shows instead of itself, its name and why, in words. Esc, Back or
/// the strip get you out like from any other example. It's here so a missing example is seen to be missing and never
/// just isn't in the list.
/// </summary>
internal sealed class UnsupportedScene : Scene
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    public UnsupportedScene(string name, string why)
    {
        var compositor = AddComponent(UICompositor.ForScreen(UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE));
        compositor.DesignSize = DesignSize;

        var card = compositor.CreateModule().AddComponent(new StackPanel
        {
            Color = HostStyle.Card,
            Radius = HostStyle.RADIUS,
            Padding = new UIEdges(32, 28),
            Spacing = 14
        });
        card.Add(new Label($"{name} can't run here") { Anchor = Origin.Left, TextScale = 0.45f, Color = HostStyle.Accent });
        card.Add(new Label(why) { Anchor = Origin.Left, Align = Origin.TopLeft, TextScale = 0.27f, Wrap = true, Size = new Vector2(900, 0) });
        card.Add(new Label("Esc or Back for the menu, Page Up and Page Down for the examples either side.") { Anchor = Origin.Left, TextScale = 0.22f, Color = HostStyle.Dim });
    }

    public override void Render(float dt)
    {
        Engine.Graphics.ClearColor = HostStyle.Backdrop;
        base.Render(dt);
    }
}
