using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Logging;

using Horizon.Core.Tweening;
using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// What a component does with the pointer and the keyboard, the popups it can show and the entrance it can make.
public abstract partial class UIComponent
{
    /* The pointer */

    /// <summary>
    /// Whether the pointer stops at this component. If it doesn't, the pointer goes through to whatever
    /// is behind, and none of the pointer callbacks are ever called.
    /// </summary>
    protected virtual bool HitTestVisible => false;

    /// <summary>
    /// Whether children are cut off at the edges of this component, which also takes whatever of them sticks
    /// out away from the pointer.
    /// </summary>
    protected virtual bool ClipsChildren => false;

    /// <summary>
    /// Whether a child is part of what is shown right now. Not shown it is neither drawn nor hit by the pointer,
    /// without anything about the child itself changing (its <see cref="Visible"/> included, which goes into the
    /// file): how the pages of a <see cref="TabPanel"/> that aren't open stay out of the way.
    /// </summary>
    protected internal virtual bool ShowsChild(UIComponent child) => true;

    /// <summary>The topmost component under a point, searching this component and everything inside it.</summary>
    internal UIComponent? HitTest(Vector2 point)
    {
        if (!Visible || IsOnHiddenLayer)
            return null;

        // The pointer is where things are drawn, which for a component that is being animated isn't where
        // the layout has them.
        if (VisualOffset != Vector2.Zero || VisualScale != Vector2.One)
        {
            if (VisualScale.X == 0.0f || VisualScale.Y == 0.0f)
                return null;

            point = Bounds.Center + (point - VisualOffset - Bounds.Center) / VisualScale;
        }

        if (ClipsChildren && !Bounds.Contains(point))
            return null;

        var snapshot = children;
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (ShowsChild(snapshot[i]) && snapshot[i].HitTest(point) is { } hit)
                return hit;
        }

        return HitTestVisible && Bounds.Contains(point) ? this : null;
    }

    /// <summary>The pointer was pressed on this component.</summary>
    protected internal virtual void OnPointerDown(Vector2 point)
    { }

    /// <summary>The pointer is still held after being pressed on this component, wherever it is by now.</summary>
    protected internal virtual void OnPointerDrag(Vector2 point)
    { }

    /// <summary>The pointer that was pressed on this component was released, anywhere.</summary>
    protected internal virtual void OnPointerUp(Vector2 point)
    { }

    /// <summary>The pointer was pressed and released on this component.</summary>
    protected internal virtual void OnClick()
    { }

    /// <summary>
    /// The mouse wheel was turned over this component or something inside it. Whoever has a use for it says
    /// so by returning true, otherwise the component it is in gets asked.
    /// </summary>
    /// <param name="delta">How many notches, positive away from the user (up).</param>
    protected internal virtual bool OnScroll(float delta) => false;

    /// <summary>
    /// What the other button of the pointer (right click) does on this component, or on anything inside it that has
    /// nothing of its own: called with where the pointer is, in the layout's units. Usually shows a
    /// <see cref="ContextMenu"/>. Null for nothing, the compositor's <see cref="UICompositor.ContextRequested"/> gets it then.
    /// </summary>
    public Action<Vector2>? OnContextMenu { get; set; }

    /* Driving the UI without a pointer: a gamepad or the keyboard walking from one component to the next, see UINavigator */

    /// <summary>
    /// Whether the component is the one a <see cref="UINavigator"/> is on. It is drawn lit the way the pointer lights
    /// it, and pressing the confirm button does what a click would (<see cref="OnActivate"/>).
    /// </summary>
    public bool IsSelected { get; internal set; }

    /// <summary>
    /// Whether a navigator stops at this component. Buttons, toggles, selectors, sliders and text boxes do, labels
    /// and panels don't.
    /// </summary>
    protected internal virtual bool Navigable => false;

    /// <summary>What the confirm button does to this component while it is selected. A click, for most things.</summary>
    protected internal virtual void OnActivate()
    { }

    /// <summary>
    /// What left and right (or a stick) do to this component while it is selected: a selector steps through its
    /// options, a slider moves. Whoever has a use for it says so by returning true, otherwise the navigator moves on.
    /// </summary>
    /// <param name="step">-1 for left, 1 for right.</param>
    protected internal virtual bool OnAdjust(int step) => false;

    /// <summary>
    /// What a direction does inside of this component while it is selected, before the navigator moves on to another
    /// one: a list box moves its choice up and down. Whoever has a use for it says so by returning true, and false at
    /// the end of what it has, so the next press leaves it.
    /// </summary>
    /// <param name="right">1 for right, -1 for left, 0 for neither.</param>
    /// <param name="down">1 for down, -1 for up, 0 for neither.</param>
    protected internal virtual bool OnNavigate(int right, int down) => false;

    /// <summary>
    /// What is written in a box next to the pointer when it rests on the component for a moment. Empty for nothing.
    /// In a layout file: <c>tooltip: "Starts the fight"</c>.
    /// </summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>
    /// Helper method to draw the rim that says "this one" around a component a navigator is on. Called by the
    /// components that have no look of their own for it, after they have painted themselves.
    /// </summary>
    protected void PaintSelection(UIDrawList list)
    {
        if (!IsSelected || !EnabledInHierarchy)
            return;

        var skin = list.Skin;
        if (skin.TryGetRegion(SELECTION_REGION, out var marker))
            list.NineSlice(marker, Bounds.Shrink(new UIEdges(-marker.Scale)), Vector4.One);
        else
            list.Frame(Bounds.Shrink(new UIEdges(-SELECTION_RIM)), 2.0f, skin.HighlightColor);
    }

    // The art a skin draws around the chosen one of a group, and how far outside of a component the rim goes without it
    internal const string SELECTION_REGION = "selection";
    private const float SELECTION_RIM = 3.0f;

    /* Popups: what a component shows on top of everything else in its module for a while, the list of a dropdown say */

    /// <summary>Whether this component is the one that has something open on top of its module.</summary>
    public bool IsPopupOpen => Module?.Popup == this;

    /// <summary>
    /// Has the module draw <see cref="PaintPopup"/> on top of everything in it and send the pointer here
    /// first, until <see cref="ClosePopup"/> or a press anywhere else. A module shows one popup at a time.
    /// </summary>
    protected void OpenPopup()
    {
        if (Module is { } owner)
            owner.Popup = this;
    }

    protected void ClosePopup()
    {
        if (Module is { } owner && owner.Popup == this)
            owner.Popup = null;
    }

    /// <summary>Draws what the component has open, after everything else in the module and cut off by nothing.</summary>
    protected internal virtual void PaintPopup(UIDrawList list)
    { }

    /// <summary>Whether a point is on what the component has open, in which case the pointer is this component's.</summary>
    protected internal virtual bool PopupContains(Vector2 point) => false;

    /* Tweens a layout asks for */

    /// <summary>How the component makes its entrance when <see cref="PlayIntro"/> is called, which loading a layout does.</summary>
    public UIIntro Intro { get; set; }

    /// <summary>How long the entrance takes, in seconds.</summary>
    public float IntroTime { get; set; } = DEFAULT_INTRO_TIME;

    /// <summary>How long the component stays hidden before its entrance starts, in seconds.</summary>
    public float IntroDelay { get; set; }

    /// <summary>Where a component that slides in comes from, relative to where it belongs: (-300, 0) is from the left.</summary>
    public Vector2 IntroOffset { get; set; }

    /// <summary>
    /// How the entrance is timed, any <see cref="Easing"/> (<c>intro_easing: "out_cubic"</c> in a layout). Null for
    /// what the entrance does by itself, a pop overshoots and settles, a fade and a slide ease out cubic.
    /// </summary>
    public Easing? IntroEasing { get; set; }

    public const float DEFAULT_INTRO_TIME = 0.35f;

    /// <summary>
    /// Plays the entrance of this component and of everything inside it that has one.
    /// </summary>
    /// <param name="delay">Seconds to wait on top of each component's own delay.</param>
    public void PlayIntro(float delay = 0.0f)
    {
        float wait = IntroDelay + delay;

        switch (Intro)
        {
            case UIIntro.Pop:
                this.PopIn(IntroTime, wait, IntroEasing ?? Easing.OutBack);
                break;

            case UIIntro.Fade:
                this.FadeIn(IntroTime, wait, IntroEasing ?? Easing.OutCubic);
                break;

            case UIIntro.Slide:
                this.SlideIn(IntroOffset, IntroTime, wait, IntroEasing ?? Easing.OutCubic);
                break;
        }

        // One after the other, for a container that says so
        float step = this is Panel panel ? panel.Stagger : 0.0f;
        var snapshot = children;

        for (int i = 0; i < snapshot.Length; i++)
            snapshot[i].PlayIntro(delay + i * step);
    }

    /* The keyboard */

    /// <summary>
    /// Whether pressing the pointer on this component gives it the focus. Pressing it anywhere else
    /// takes the focus away again.
    /// </summary>
    protected internal virtual bool Focusable => false;

    /// <summary>
    /// Makes this the component the keyboard types into, whether it is <see cref="Focusable"/> by the
    /// pointer or not. Does nothing while the component isn't in a module.
    /// </summary>
    public void Focus()
    {
        if (Module is { } owner)
            owner.Compositor.Focus = this;
    }

    /// <summary>Gives the focus up, if this component has it.</summary>
    public void Unfocus()
    {
        if (IsFocused)
            Module!.Compositor.Focus = null;
    }

    /// <summary>
    /// A character was typed while this component had the focus. Backspace arrives as '\b' and
    /// enter as '\n'.
    /// </summary>
    protected internal virtual void OnTextInput(char character)
    { }

    /// <summary>
    /// Text was pasted (control with V) while this component had the focus. A text box types what fits of it,
    /// anything else does nothing with it unless it says otherwise.
    /// </summary>
    protected internal virtual void OnPaste(string text)
    { }
}
