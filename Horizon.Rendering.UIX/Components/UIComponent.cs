using System.Numerics;
using System.Runtime.CompilerServices;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using Horizon.Core.Tweening;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// Which axes a component stretches along to fill the space its parent gives it.
/// </summary>
[Flags]
public enum UIFill
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Both = Horizontal | Vertical
}

/// <summary>
/// The ways a component can make its entrance, see <see cref="UIComponent.Intro"/>. In a layout file:
/// <c>intro: "pop"</c>, or <c>intro: "slide", intro_offset: vec(-300, 0)</c> to come in from the left.
/// </summary>
public enum UIIntro
{
    /// <summary>It is simply there.</summary>
    None,

    /// <summary>Grows out of nothing, a little too far and back.</summary>
    Pop,

    Fade,

    /// <summary>Slides into its place from <see cref="UIComponent.IntroOffset"/>.</summary>
    Slide
}

/// <summary>
/// The base of everything in a UI: a node in a tree that knows where it sits inside its parent, how to
/// paint itself and what to do with the pointer.
/// A component owns nothing on the GPU. The compositor lays the tree out and paints it on the logic
/// thread, so a component can be created, changed and moved around at any time, from C# or from a script.
/// </summary>
public abstract class UIComponent
{
    // Finds the component behind an object a script is holding.
    private static readonly ConditionalWeakTable<Dictionary<string, IRuntimeValue>, UIComponent> scripted = new();

    // Children are replaced wholesale on every change, so the logic thread can walk the array it has
    // while another thread adds to the tree.
    private readonly Lock childrenLock = new();
    private UIComponent[] children = [];

    private TweenContext? tweens;

    // Where it was drawn last, there once something wants to know how fast it is going (see UIDrawList.PushMotion)
    private UIMotion? motion;

    private UIModule? module;
    private Dictionary<string, IRuntimeValue>? script;

    public UIComponent? Parent { get; private set; }

    /// <summary>The components inside this one, drawn in order so the last is on top.</summary>
    public IReadOnlyList<UIComponent> Children => children;

    /// <summary>
    /// The children as a span, for the code that walks them every update: unlike enumerating
    /// <see cref="Children"/> it doesn't allocate.
    /// </summary>
    protected ReadOnlySpan<UIComponent> ChildSpan => children;

    /// <summary>The module this component is part of, null while it isn't in one.</summary>
    public UIModule? Module
    {
        get => module ?? Parent?.Module;
        internal set => module = value;
    }

    /// <summary>The point of the parent this component is attached to.</summary>
    public Origin Anchor { get; set; } = Origin.Center;

    /// <summary>
    /// The point of this component that is put on the anchor. Left unset it is the same point as
    /// <see cref="Anchor"/>, so a component anchored to the top left sits in its parent's top left corner.
    /// </summary>
    public Origin? Pivot { get; set; }

    /// <summary>How far from the anchor the component sits. Y points up.</summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// The size of the component. An axis left at zero is sized automatically, to whatever
    /// the component needs.
    /// </summary>
    public Vector2 Size { get; set; }

    /// <summary>Multiplies the automatic size.</summary>
    public Vector2 Scale { get; set; } = Vector2.One;

    public UIFill Fill { get; set; }

    /// <summary>The space kept clear between the edges of this component and its children.</summary>
    public UIEdges Padding { get; set; }

    /// <summary>
    /// Moves where the component and everything inside it is drawn, away from where the layout put it.
    /// Nothing around it moves along: this is for animating, see <see cref="UITweens"/>. Y points up.
    /// </summary>
    public Vector2 VisualOffset { get; set; }

    /// <summary>
    /// Scales how the component and everything inside it is drawn, around the middle of its bounds.
    /// The layout doesn't notice, unlike with <see cref="Scale"/>.
    /// </summary>
    public Vector2 VisualScale { get; set; } = Vector2.One;

    /// <summary>How see-through the component and everything inside it is drawn, from 0 (not at all) to 1.</summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// The tweens that are animating this component, moved along once per update for as long as the component
    /// is in a UI. See <see cref="UITweens"/> for the ones that come ready made.
    /// </summary>
    public TweenContext Tweens => tweens ?? Interlocked.CompareExchange(ref tweens, new TweenContext(), null) ?? tweens;

    /// <summary>An invisible component, and everything inside it, is neither drawn nor hit by the pointer.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// The layer the component is on, with everything inside it that doesn't name one of its own. Empty for none.
    /// A layer is a name several parts of a layout share so they can be shown and hidden together, see
    /// <see cref="UIModule.SetLayerVisible"/>: the screens of a menu that are laid over each other in one file,
    /// or an overlay that is only up some of the time. A component on a hidden layer is neither drawn nor hit
    /// by the pointer, but keeps its place in the layout and whatever <see cref="Visible"/> says.
    /// </summary>
    public string Layer { get; set; } = string.Empty;

    /// <summary>The layer the component is on by what it or the nearest thing around it says, empty for none.</summary>
    public string EffectiveLayer
    {
        get
        {
            for (UIComponent? at = this; at is not null; at = at.Parent)
            {
                if (at.Layer.Length > 0)
                    return at.Layer;
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Whether the component names a layer that is hidden in its module. Whatever is inside of it is hidden with
    /// it, which this doesn't say for them: see <see cref="IsHiddenByLayer"/>.
    /// </summary>
    public bool IsOnHiddenLayer => Layer.Length > 0 && Module is { } owner && owner.IsLayerHidden(Layer);

    /// <summary>Whether the component is out of sight because it, or something it is inside of, is on a hidden layer.</summary>
    public bool IsHiddenByLayer
    {
        get
        {
            for (UIComponent? at = this; at is not null; at = at.Parent)
            {
                if (at.IsOnHiddenLayer)
                    return true;
            }

            return false;
        }
    }

    /// <summary>A disabled component, and everything inside it, ignores the pointer.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the component ended up when it was last laid out, in its module's space.</summary>
    public UIRect Bounds { get; private set; }

    /// <summary>The size the component asked for when it was last laid out.</summary>
    public Vector2 DesiredSize { get; private set; }

    /// <summary>Whether the pointer is over this component.</summary>
    public bool IsHovered { get; internal set; }

    /// <summary>Whether the pointer was pressed on this component and hasn't been released yet.</summary>
    public bool IsPressed { get; internal set; }

    /// <summary>Whether this component is the one the keyboard types into.</summary>
    public bool IsFocused => Module?.Compositor.Focus == this;

    /// <summary>Whether this component and everything above it is enabled.</summary>
    public bool EnabledInHierarchy
    {
        get
        {
            for (UIComponent? component = this; component is not null; component = component.Parent)
            {
                if (!component.Enabled)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The object scripts see this component as. Its properties are the ones declared in <see cref="DefineScript"/>.
    /// </summary>
    public ObjectValue Object
    {
        get
        {
            if (script is null)
            {
                script = [];
                scripted.Add(script, this);
                DefineScript();
            }
            return new ObjectValue(script);
        }
    }

    /// <summary>The component a script object belongs to, null if it isn't one of ours.</summary>
    public static UIComponent? FromScript(IRuntimeValue? value) =>
        value is ObjectValue { Properties: not null } obj && scripted.TryGetValue(obj.Properties, out var component)
            ? component
            : null;

    /// <summary>
    /// Puts a component inside this one, taking it out of wherever it was before.
    /// </summary>
    public T Add<T>(T child) where T : UIComponent
    {
        for (UIComponent? component = this; component is not null; component = component.Parent)
        {
            if (component == child)
                throw new InvalidOperationException("A component cannot be put inside itself.");
        }

        child.Parent?.Remove(child);

        lock (childrenLock)
            children = [.. children, child];

        child.Parent = this;
        return child;
    }

    /// <summary>
    /// Puts a child somewhere else among its siblings, which is the order they are drawn and laid out in.
    /// </summary>
    /// <param name="index">Where it goes, counted from 0. Past the end is the end.</param>
    public bool MoveChild(UIComponent child, int index)
    {
        lock (childrenLock)
        {
            int from = Array.IndexOf(children, child);
            if (from < 0)
                return false;

            var moved = new List<UIComponent>(children);
            moved.RemoveAt(from);
            moved.Insert(Math.Clamp(index, 0, moved.Count), child);
            children = [.. moved];
        }

        return true;
    }

    /// <summary>
    /// Puts a component inside this one at a place among its children, see <see cref="Add"/> and <see cref="MoveChild"/>.
    /// </summary>
    public T Insert<T>(int index, T child) where T : UIComponent
    {
        Add(child);
        MoveChild(child, index);
        return child;
    }

    public bool Remove(UIComponent child)
    {
        lock (childrenLock)
        {
            int index = Array.IndexOf(children, child);
            if (index < 0)
                return false;

            children = [.. children[..index], .. children[(index + 1)..]];
        }

        child.Parent = null;
        return true;
    }

    /* Layout: sizes are worked out from the leaves up, then places are handed out from the root down. */

    internal Vector2 MeasureTree(UISkin skin)
    {
        foreach (var child in children)
            child.MeasureTree(skin);

        Vector2 automatic = Measure(skin) * Scale;
        DesiredSize = new Vector2(
            Size.X > 0.0f ? Size.X : automatic.X,
            Size.Y > 0.0f ? Size.Y : automatic.Y);

        return DesiredSize;
    }

    internal void ArrangeTree(UIRect bounds)
    {
        Bounds = bounds;
        Arrange(bounds.Shrink(Padding));
    }

    /// <summary>
    /// The size the component needs when <see cref="Size"/> leaves it up to the component. Its children
    /// have been measured by the time this is called.
    /// </summary>
    protected virtual Vector2 Measure(UISkin skin) => Vector2.Zero;

    /// <summary>
    /// Gives every child its place inside <paramref name="content"/>, which is this component's bounds
    /// less its padding. By default each child goes where its own anchor and position put it.
    /// </summary>
    protected virtual void Arrange(UIRect content)
    {
        foreach (var child in children)
            child.ArrangeTree(child.Place(content));
    }

    /// <summary>
    /// Where the component's anchor, pivot, position and fill put it inside an area.
    /// </summary>
    protected internal UIRect Place(UIRect area)
    {
        Vector2 size = DesiredSize;
        Vector2 anchor = Anchor.ToVector();
        Vector2 pivot = (Pivot ?? Anchor).ToVector();

        // Stretching along an axis leaves nothing to anchor on it.
        if (Fill.HasFlag(UIFill.Horizontal))
        {
            size.X = area.Width;
            anchor.X = pivot.X = 0.0f;
        }
        if (Fill.HasFlag(UIFill.Vertical))
        {
            size.Y = area.Height;
            anchor.Y = pivot.Y = 0.0f;
        }

        Vector2 center = area.Center + anchor * area.Size + Position - pivot * size;
        return UIRect.FromCenter(center, size);
    }

    /* Painting */

    internal void PaintTree(UIDrawList list)
    {
        if (!Visible || Opacity <= 0.0f || IsOnHiddenLayer)
            return;

        bool animated = VisualOffset != Vector2.Zero || VisualScale != Vector2.One || Opacity < 1.0f;
        if (animated)
            list.PushVisual(VisualOffset, VisualScale, Bounds.Center, Opacity);

        // What is painted from here on goes as fast as this component does, until a child says how fast it goes
        bool tracked = list.TracksMotion;
        if (tracked)
            list.PushMotion(motion ??= new UIMotion(), Bounds);

        Paint(list);
        PaintChildren(list);

        if (tracked)
            list.PopMotion();
        if (animated)
            list.PopVisual();
    }

    /// <summary>
    /// Draws the children, on top of the component and in their order. A component that wants something
    /// around all of them (a clip) or on top of all of them (a scroll bar) does it here.
    /// </summary>
    protected virtual void PaintChildren(UIDrawList list)
    {
        foreach (var child in children)
            child.PaintTree(list);
    }

    /// <summary>
    /// Draws the component into its <see cref="Bounds"/>. Its children are drawn afterwards, on top.
    /// </summary>
    protected virtual void Paint(UIDrawList list)
    { }

    /* Updating */

    internal void UpdateTree(float dt)
    {
        tweens?.Tick(dt);
        Update(dt);

        foreach (var child in children)
            child.UpdateTree(dt);
    }

    /// <summary>Called once per state update, on the logic thread.</summary>
    protected virtual void Update(float dt)
    { }

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
            if (snapshot[i].HitTest(point) is { } hit)
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
                this.PopIn(IntroTime, wait);
                break;

            case UIIntro.Fade:
                this.FadeIn(IntroTime, wait);
                break;

            case UIIntro.Slide:
                this.SlideIn(IntroOffset, IntroTime, wait);
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

    /* Scripting */

    /// <summary>
    /// Declares the properties scripts can read and write on this component, with the Expose methods.
    /// </summary>
    protected virtual void DefineScript()
    {
        Expose("pos", () => Position, value => Position = value);
        Expose("size", () => Size, value => Size = value);
        Expose("scale", () => Scale, value => Scale = value);
        Expose("visible", () => Visible, value => Visible = value);
        Expose("layer", () => Layer, value => Layer = value.Trim());
        Expose("enabled", () => Enabled, value => Enabled = value);

        Expose(
            "anchor",
            () => UIScript.FromEnum(Anchor),
            value => Anchor = UIScript.ToEnum<Origin>(value, "anchor"));
        Expose(
            "pivot",
            () => UIScript.FromEnum(Pivot ?? Anchor),
            value => Pivot = UIScript.ToEnum<Origin>(value, "pivot"));
        Expose(
            "fill",
            () => UIScript.FromEnum(Fill),
            value => Fill = UIScript.ToEnum<UIFill>(value, "fill"));
        Expose(
            "padding",
            () => UIScript.FromEdges(Padding),
            value => Padding = UIScript.ToEdges(value, "padding"));

        Expose(
            "intro",
            () => UIScript.FromEnum(Intro),
            value => Intro = UIScript.ToEnum<UIIntro>(value, "intro"));
        Expose("intro_time", () => IntroTime, value => IntroTime = MathF.Max(0.0f, value));
        Expose("intro_delay", () => IntroDelay, value => IntroDelay = MathF.Max(0.0f, value));
        Expose("intro_offset", () => IntroOffset, value => IntroOffset = value);

        Expose(
            "parent",
            () => Parent is { } parent ? parent.Object : (IRuntimeValue)new NullValue(),
            value =>
            {
                var parent = FromScript(value) ?? throw new Exception("parent has to be a component.");
                parent.Add(this);
            });
    }

    /// <summary>Makes a property available to scripts under a name, as whatever value the callbacks deal in.</summary>
    protected void Expose(string name, Func<IRuntimeValue> get, Action<IRuntimeValue> set) =>
        script![name] = new NativeValue(get, set);

    protected void Expose(string name, Func<float> get, Action<float> set) =>
        Expose(name, () => new NumberValue(get()), value => set(UIScript.ToNumber(value, name)));

    protected void Expose(string name, Func<bool> get, Action<bool> set) =>
        Expose(name, () => new BooleanValue(get()), value => set(UIScript.ToBoolean(value, name)));

    protected void Expose(string name, Func<string> get, Action<string> set) =>
        Expose(name, () => new StringValue(get()), value => set(UIScript.ToText(value, name)));

    protected void Expose(string name, Func<Vector2> get, Action<Vector2> set) =>
        Expose(name, () => new Vector2Value(get()), value => set(UIScript.ToVector2(value, name)));

    protected void Expose(string name, Func<Vector4> get, Action<Vector4> set) =>
        Expose(name, () => new Vector4Value(get()), value => set(UIScript.ToColor(value, name)));

    /// <summary>
    /// Sets every property named in an object a script wrote, e.g. { label: "Play", pos: vec(0, 32) }.
    /// </summary>
    internal void ApplyScript(ObjectValue values)
    {
        var properties = Object.Properties;

        foreach (var (name, value) in values.Properties)
        {
            if (!properties.TryGetValue(name, out var property) || property is not NativeValue native)
                throw new Exception($"{GetType().Name} has no property called '{name}'.");

            native.MutatorCallback(value);
        }
    }

    /// <summary>
    /// Calls a function a script assigned to one of this component's handlers. Errors in the script are
    /// logged rather than thrown, so a broken handler can't take the game down with it.
    /// </summary>
    protected void InvokeScript(IRuntimeValue? handler, params IRuntimeValue[] arguments)
    {
        if (handler is null or NullValue || Module is not { } owner)
            return;

        try
        {
            owner.Runtime.Interpreter.Call(handler, arguments, owner.Runtime.UserScope);
        }
        catch (Exception e)
        {
            ConcurrentLogger.Instance.Log(LogLevel.Error, $"[UIX] A {GetType().Name} handler failed: {e.Message}");
        }
    }
}
