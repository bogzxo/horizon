using CoreAnimation;

using Foundation;

using ObjCRuntime;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// The view the engine draws into, a UIView whose own layer is a CAMetalLayer, which MoltenVK makes the Vulkan surface
/// on. Its touches go to the engine as fingers, pens and a pointer (an iPad's trackpad or mouse). While the UI wants
/// text it's the first responder and takes typing through UIKeyInput, which is what puts the soft keyboard up.
/// </summary>
public sealed class HorizonMetalView : UIView, IUIKeyInput
{
    private readonly Func<IosInput?> input;
    private bool textActive;

    /// <summary>The one view made in this process, for the soft keyboard to be asked for from the host.</summary>
    internal static HorizonMetalView? Current { get; private set; }

    /// <summary>Says to UIKit that this view's layer is a Metal layer, made by UIKit along with the view.</summary>
    [Export("layerClass")]
    public static Class GetLayerClass() => new(typeof(CAMetalLayer));

    /// <summary>The view's layer, the one the engine's Vulkan surface is made on.</summary>
    public CAMetalLayer MetalLayer => (CAMetalLayer)Layer;

    internal HorizonMetalView(Func<IosInput?> input)
    {
        this.input = input;
        Current = this;

        MultipleTouchEnabled = true;
        Opaque = true;
        BackgroundColor = UIColor.Black;

        // Every pixel of the screen, not points stretched up. The layer's drawable size follows the swapchain
        nfloat scale = UIScreen.MainScreen.NativeScale;
        ContentScaleFactor = scale;
        MetalLayer.ContentsScale = scale;
        MetalLayer.Opaque = true;
        MetalLayer.FramebufferOnly = true;

        // An iPad's trackpad or mouse moving with nothing pressed
        AddGestureRecognizer(new UIHoverGestureRecognizer(Hovered));
    }

    public override void TouchesBegan(NSSet touches, UIEvent? evt) => input()?.Touches(this, touches, IosPointers.Phase.Began);

    public override void TouchesMoved(NSSet touches, UIEvent? evt) => input()?.Touches(this, touches, IosPointers.Phase.Moved);

    public override void TouchesEnded(NSSet touches, UIEvent? evt) => input()?.Touches(this, touches, IosPointers.Phase.Ended);

    public override void TouchesCancelled(NSSet touches, UIEvent? evt) => input()?.Touches(this, touches, IosPointers.Phase.Cancelled);

    private void Hovered(UIHoverGestureRecognizer hover) => input()?.Hover(hover.LocationInView(this));

    /// <summary>First responder only while the UI wants text, which is what has UIKit show the soft keyboard.</summary>
    public override bool CanBecomeFirstResponder => textActive;

    /// <summary>The soft keyboard up or away. Main thread.</summary>
    internal void SetTextActive(bool active)
    {
        if (textActive == active) return;
        textActive = active;
        if (active) BecomeFirstResponder();
        else
        {
            ResignFirstResponder();

            // The keys of a hardware keyboard go to the controller again
            NextResponder?.BecomeFirstResponder();
        }
    }

    [Export("hasText")]
    public bool HasText => true;

    [Export("insertText:")]
    public void InsertText(string text) => input()?.TypeFromKeyboard(text);

    [Export("deleteBackward")]
    public void DeleteBackward() => input()?.Tap(Horizon.Input.Key.Backspace);

    // What the soft keyboard should be like for a game's text field, nothing guessed or corrected
    [Export("autocorrectionType")]
    public UITextAutocorrectionType AutocorrectionType => UITextAutocorrectionType.No;

    [Export("autocapitalizationType")]
    public UITextAutocapitalizationType AutocapitalizationType => UITextAutocapitalizationType.None;

    [Export("spellCheckingType")]
    public UITextSpellCheckingType SpellCheckingType => UITextSpellCheckingType.No;

    protected override void Dispose(bool disposing)
    {
        if (disposing && Current == this) Current = null;
        base.Dispose(disposing);
    }
}
