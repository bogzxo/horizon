using System.Numerics;

using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI.Skinning;

using Silk.NET.Input;

namespace Horizon.UI.Components;

/// <summary>
/// A mode laid over the whole of a module for moving and sizing one rectangle by hand, a picture behind something say.
/// The rectangle gets a frame with handles on its corners and edges, a drag inside it moves it, a handle sizes it
/// (keeping its shape, Alt lets go of that) and the wheel scales it round the pointer. A little bar says so with Done,
/// Reset and Cancel on it, Enter is Done and Escape is Cancel. Nothing under it gets the pointer while it's up.
/// <code>
/// var arranging = module.Root.Add(new ArrangeOverlay { Rect = placed, Picture = picture, Room = area });
/// arranging.OnChanged = rect => placed = rect;
/// arranging.OnDone = () => Save(placed);
/// arranging.Focus();
/// </code>
/// The owner hears every change and can put <see cref="Rect"/> back where it allows (a placement it clamps), the
/// overlay takes it from there. Taking it out of the module is the owner's business too, after Done or Cancel.
/// </summary>
public sealed partial class ArrangeOverlay : UIComponent
{
    /// <summary>The smallest the rectangle can be sized down to, in the layout's units, either way.</summary>
    public const float LEAST = 24.0f;

    /// <summary>Where the rectangle is now, in the module's units (y up).</summary>
    public UIRect Rect { get; set; }

    /// <summary>
    /// The room the rectangle is arranged in, its picture cut off at its edges and the bar over its top middle. Null for
    /// the whole of the overlay.
    /// </summary>
    public UIRect? Room { get; set; }

    /// <summary>A picture drawn in the rectangle while it's arranged, so it can be seen whatever is in front of it. Null for none.</summary>
    public Texture? Picture { get; set; }

    /// <summary>How much of the <see cref="Picture"/> shows.</summary>
    public float PictureOpacity { get; set; } = 0.7f;

    /// <summary>Called with the rectangle every time a drag, a handle, the wheel or an arrow key moved it.</summary>
    public Action<UIRect>? OnChanged { get; set; }

    /// <summary>Done or Enter, keep it.</summary>
    public Action? OnDone { get; set; }

    /// <summary>Reset, the owner puts it back where it goes by itself and sets <see cref="Rect"/> to that.</summary>
    public Action? OnReset { get; set; }

    /// <summary>Cancel or Escape, put back what was there before.</summary>
    public Action? OnCancel { get; set; }

    /// <summary>The bar with the hint and the buttons, for the owner to dress (its glass, its colour).</summary>
    public Panel Bar { get; }

    /// <summary>The words on the bar saying what to do.</summary>
    public Label Hint { get; }

    public Button DoneButton { get; }
    public Button ResetButton { get; }
    public Button CancelButton { get; }

    // How far the bar sits down from the top of the room
    private const float BAR_GAP = 12.0f;

    public ArrangeOverlay()
    {
        Fill = UIFill.Both;
        ZOffset = 100;

        Bar = Add(new Panel { Anchor = Origin.Top, Pivot = Origin.Top, Radius = 12.0f, Padding = new UIEdges(12.0f, 6.0f, 6.0f, 6.0f) });
        StackPanel row = Bar.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 6.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        Hint = row.Add(new Label("Drag to move it, its handles size it, the wheel scales it. Alt frees its shape."));
        row.Add(new Spacer { Size = new Vector2(6.0f, 1.0f) });
        DoneButton = row.Add(new Button("Done") { Primary = true, OnPressed = () => OnDone?.Invoke() });
        ResetButton = row.Add(new Button("Reset") { OnPressed = Reset });
        CancelButton = row.Add(new Button("Cancel") { OnPressed = () => OnCancel?.Invoke() });
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Focusable => true;

    // Its keys come as themselves, so Escape and Enter are its own and the compositor leaves them be
    protected internal override bool TakesRawKeys => true;

    protected internal override void OnKeyDown(in UIKeyPress press)
    {
        float step = press.Shift ? 10.0f : 1.0f;
        switch (press.Key)
        {
            case Key.Escape:
                OnCancel?.Invoke();
                break;
            case Key.Enter or Key.KeypadEnter:
                OnDone?.Invoke();
                break;
            case Key.Left:
                Nudge(new Vector2(-step, 0.0f));
                break;
            case Key.Right:
                Nudge(new Vector2(step, 0.0f));
                break;
            case Key.Up:
                Nudge(new Vector2(0.0f, step));
                break;
            case Key.Down:
                Nudge(new Vector2(0.0f, -step));
                break;
        }
    }

    // The arrows move it a pixel at a time, ten with Shift
    private void Nudge(Vector2 by)
    {
        if (!Rect.IsEmpty)
            Change(new UIRect(Rect.Min + by, Rect.Max + by));
    }

    // Reset keeps the mode up, the owner's put it back and the keyboard comes back here from the button
    private void Reset()
    {
        OnReset?.Invoke();
        Focus();
    }

    /// <summary>Helper method to take a new rectangle and tell the owner, who may put it somewhere else again.</summary>
    private void Change(UIRect rect)
    {
        Rect = rect;
        OnChanged?.Invoke(rect);
    }

    protected override void Update(float dt)
    {
        base.Update(dt);

        // The bar over the top middle of the room, wherever that is in the overlay
        UIRect room = Room ?? Bounds;
        Bar.Position = new Vector2(room.Center.X - Bounds.Center.X, room.Max.Y - Bounds.Max.Y - BAR_GAP);
        if (Bar.Color is null && Module?.Compositor.Skin is UISkin skin)
            Bar.Color = skin.Palette.Raised;

        // The keyboard stays here while it's up, a press on one of the bar's buttons let go of it
        if (Module is { } module && module.Compositor.Focus is null)
            Focus();

        UpdateHover();
    }
}
