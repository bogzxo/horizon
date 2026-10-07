namespace Horizon.Rendering.UIX;

/// <summary>
/// What of its layout a UI draws over itself, for whoever is working out why it is laid out the way it is. The
/// edges of every component, the padding inside of them, the gaps stacks leave between their children, and what
/// the pointer is over along with its size. A UI draws all of that while it is handed one of these
/// (<see cref="UICompositor.LayoutOverlay"/>), in any build: the drawing is the UI's, which is the one that knows
/// where its components are on screen, what to draw is up to whoever asks. Horizon.Hex is what does.
/// </summary>
public sealed class UILayoutOverlayOptions
{
    /// <summary>Outlines every component.</summary>
    public bool ShowBounds { get; set; } = true;

    /// <summary>Fills in the space components keep clear inside of their edges.</summary>
    public bool ShowPadding { get; set; } = true;

    /// <summary>Fills in the gaps stacks leave between their children.</summary>
    public bool ShowSpacing { get; set; } = true;

    /// <summary>Writes what the pointer is over, and how big it is, next to it.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Only draws the component the pointer is over and the ones it is inside of, for busy screens.</summary>
    public bool HoveredOnly { get; set; }

    /// <summary>Also draws the components that are hidden, which still take up their place in some layouts.</summary>
    public bool ShowHidden { get; set; }
}
