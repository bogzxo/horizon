namespace Horizon.UI;

/// <summary>
/// How a module that was designed for one screen (see <see cref="UIModule.DesignSize"/>) goes onto a screen of
/// another shape. Phones, ultrawides, a window somebody dragged into a silly shape: the layout has to cope.
/// </summary>
public enum UIFit
{
    /// <summary>
    /// The design keeps its shape, scaled until it just fits and sat in the middle of the screen. Whatever is
    /// anchored to an edge of the layout goes to that edge of the design, not of the screen, so a menu made for
    /// 16:9 looks the same on 21:9 with room either side. What menus and dialogs want.
    /// </summary>
    Contain,

    /// <summary>
    /// Scaled the same way, but the layout gets the whole screen to itself: anchoring to an edge goes to the edge
    /// of the screen, and a wider screen is more room. What a HUD wants, its health bars up in the corners.
    /// </summary>
    Stretch,
}
