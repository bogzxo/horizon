using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

/// <summary>
/// Helps with working out why a layout is laid out the way it is. While it is on, the layout that is being edited
/// draws its own layout over itself: the edges of every component, the padding inside of them, the gaps between
/// the children of a stack, and what the pointer is over along with its size.
/// The drawing is done by the UI the layouts are in (it is the one that knows where its components are on screen),
/// this decides whether it does and what of it, and puts the switches for that into a menu. It is part of the
/// editor rather than of the engine, and there in every build of it.
/// </summary>
internal sealed class HexLayoutDebugger(UICompositor stage)
{
    /// <summary>What is drawn while the debugger is on, each of which the menu switches by itself.</summary>
    public UILayoutOverlayOptions Options { get; } = new();

    /// <summary>Whether the layouts are drawing their layout over themselves right now.</summary>
    public bool IsOn
    {
        get => stage.LayoutOverlay is not null;
        set => stage.LayoutOverlay = value ? Options : null;
    }

    public void Toggle() => IsOn = !IsOn;

    /// <summary>
    /// Adds the switch of the debugger to a menu, and under it one for everything it can draw. Those can only be
    /// picked while it is on.
    /// </summary>
    /// <param name="toggled">Called after the debugger was switched on or off from the menu, for whoever has something to say about it.</param>
    public void AddTo(Menu menu, Action? toggled = null)
    {
        menu.Add("Layout debugger", () =>
        {
            Toggle();
            toggled?.Invoke();
        }).IsChecked = () => IsOn;

        Option(menu, "    Edges", () => Options.ShowBounds, on => Options.ShowBounds = on);
        Option(menu, "    Padding", () => Options.ShowPadding, on => Options.ShowPadding = on);
        Option(menu, "    Gaps", () => Options.ShowSpacing, on => Options.ShowSpacing = on);
        Option(menu, "    Sizes", () => Options.ShowLabels, on => Options.ShowLabels = on);
        Option(menu, "    Only under the pointer", () => Options.HoveredOnly, on => Options.HoveredOnly = on);
        Option(menu, "    Hidden components too", () => Options.ShowHidden, on => Options.ShowHidden = on);
    }

    private void Option(Menu menu, string label, Func<bool> read, Action<bool> write)
    {
        MenuItem item = menu.Add(label, () => write(!read()));
        item.IsChecked = read;
        item.IsEnabled = () => IsOn;
    }
}
