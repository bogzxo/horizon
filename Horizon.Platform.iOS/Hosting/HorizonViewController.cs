using Foundation;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// The app's one view controller. It owns the Metal view, keeps the status bar and the home indicator out of the
/// game's way, tells the session every time the view is laid out, and takes a hardware keyboard's keys (it's the first
/// responder whenever the view isn't taking text).
/// </summary>
public sealed class HorizonViewController : UIViewController
{
    private readonly IosSession session;
    private UILabel? message;

    internal HorizonViewController(IosSession session) => this.session = session;

    /// <summary>The Metal view, there from LoadView on.</summary>
    public HorizonMetalView? MetalView { get; private set; }

    public override void LoadView()
    {
        MetalView = new HorizonMetalView(() => session.Engine is not null ? session.Host?.Input : null);
        View = MetalView;
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        if (MetalView is { } view && message is null) session.ViewLaidOut(view);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        BecomeFirstResponder();
    }

    public override bool CanBecomeFirstResponder => true;

    public override bool PrefersStatusBarHidden() => true;

    public override bool PrefersHomeIndicatorAutoHidden => true;

    // A swipe from an edge goes to the game first, a second one to the system
    public override UIRectEdge PreferredScreenEdgesDeferringSystemGestures => UIRectEdge.All;

    public override void PressesBegan(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (session.Host?.Input is not { } input || !input.Presses(presses, down: true)) base.PressesBegan(presses, evt);
    }

    public override void PressesEnded(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (session.Host?.Input is not { } input || !input.Presses(presses, down: false)) base.PressesEnded(presses, evt);
    }

    public override void PressesCancelled(NSSet<UIPress> presses, UIPressesEvent evt)
    {
        if (session.Host?.Input is not { } input || !input.Presses(presses, down: false)) base.PressesCancelled(presses, evt);
    }

    /// <summary>Puts a message over the view, what went wrong or that the game is over. Main thread.</summary>
    internal void ShowMessage(string text)
    {
        if (View is not { } view) return;
        message ??= new UILabel
        {
            TextColor = UIColor.White,
            BackgroundColor = UIColor.Black,
            Lines = 0,
            TextAlignment = UITextAlignment.Center,
            Font = UIFont.GetMonospacedSystemFont(13, UIFontWeight.Regular)!,
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions
        };
        message.Frame = view.Bounds.Inset(24, 48);
        message.Text = text;
        if (message.Superview is null) view.AddSubview(message);
    }
}
