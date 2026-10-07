using System.Numerics;

using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX
{
    /// <summary>
    /// A self contained piece of UI: a tree of components, the script runtime that can build and drive
    /// it, and a position and scale for the lot. A HUD, a menu and a dialog would each be a module.
    /// The components are laid out against the whole screen, so anchoring one to a corner of the
    /// module puts it in that corner of the screen.
    /// </summary>
    public partial class UIModule
    {
        /// <summary>A disabled module is neither updated, drawn nor hit by the pointer.</summary>
        public bool Enabled { get; set; } = true;

        public UICompositor Compositor { get; init; }

        /// <summary>The invisible, screen sized component everything in the module hangs off.</summary>
        public Panel Root { get; } = new();

        // The layers that are hidden. Replaced wholesale on every change, so whoever is painting can go on
        // reading the one it has
        private HashSet<string> hiddenLayers = [];

        /// <summary>The layers that are hidden right now, see <see cref="SetLayerVisible"/>.</summary>
        public IReadOnlyCollection<string> HiddenLayers => hiddenLayers;

        /// <summary>
        /// Every layer the components of the module name (see <see cref="UIComponent.Layer"/>), in the order
        /// they are first come across going through the layout.
        /// </summary>
        public IReadOnlyList<string> Layers
        {
            get
            {
                var found = new List<string>();
                Collect(Root, found);
                return found;

                static void Collect(UIComponent component, List<string> into)
                {
                    if (component.Layer.Length > 0 && !into.Contains(component.Layer))
                        into.Add(component.Layer);

                    foreach (UIComponent child in component.Children)
                        Collect(child, into);
                }
            }
        }

        /// <summary>Whether a layer is hidden. One that nobody has hidden is not, whether anything is on it or not.</summary>
        public bool IsLayerHidden(string layer) => hiddenLayers.Count > 0 && hiddenLayers.Contains(layer);

        /// <summary>
        /// Shows or hides everything on a layer. The components that name it and whatever is inside of them.
        /// What is hidden this way is neither drawn nor hit by the pointer. Nothing about the components
        /// themselves changes, so this is as good for an editor putting things out of the way while something
        /// under them is worked on as it is for a game switching between the screens of one layout.
        /// </summary>
        public void SetLayerVisible(string layer, bool visible)
        {
            if (layer.Length == 0 || visible == !hiddenLayers.Contains(layer))
                return;

            var changed = new HashSet<string>(hiddenLayers);
            if (visible)
                changed.Remove(layer);
            else
                changed.Add(layer);

            hiddenLayers = changed;
        }

        /// <summary>Shows every layer again.</summary>
        public void ShowAllLayers() => hiddenLayers = [];

        public IReadOnlyList<UIComponent> Components => Root.Children;

        /// <summary>Moves the whole module, in the world space of the compositor's camera.</summary>
        public Vector2 Position { get; set; }

        /// <summary>
        /// Scales the whole module. Components keep their places relative to the screen. One anchored
        /// to a corner stays in that corner and simply gets bigger.
        /// </summary>
        public Vector2 Scale { get; set; } = Vector2.One;

        /// <summary>
        /// The screen the module was designed for, in units of its layout: a layout file says so with
        /// <c>compositor.design({ size: vec(1600, 900), fit: "contain" })</c>. Null for a module that just takes
        /// whatever screen it's given at the compositor's scale (and its <see cref="UICompositor.DesignSize"/>).
        /// How it is fitted to the real screen is <see cref="Fit"/>.
        /// </summary>
        public Vector2? DesignSize { get; set; }

        /// <summary>
        /// How a module with a <see cref="DesignSize"/> is put on a screen that is another shape. See <see cref="UIFit"/>.
        /// </summary>
        public UIFit Fit { get; set; } = UIFit.Contain;

        /// <summary>
        /// How many units of the camera a unit of the layout comes to right now: the compositor's
        /// <see cref="UICompositor.UIScale"/>, or for a module with a <see cref="DesignSize"/> whatever fits that
        /// design to the screen times the player's <see cref="UICompositor.Scale"/>. Worked out every update.
        /// </summary>
        public float UnitScale { get; private set; } = 1.0f;

        /// <summary>
        /// The rectangle (in the module's own units) the components were laid out in last: the screen as the
        /// module sees it, or for a contained design the design itself sitting in the middle of it. What an
        /// editor draws a box around.
        /// </summary>
        public UIRect Frame { get; private set; }

        /// <summary>
        /// Draws a box around <see cref="Frame"/> in this colour, over everything in the module. For an editor
        /// that wants the edges of the layout shown; null (no box) for anything else.
        /// </summary>
        public Vector4? FrameColor { get; set; }

        /// <summary>
        /// The screen the module is laid out against, in its own space. Left unset that is whatever the camera
        /// sees; set, the module is laid out as if that were the screen whatever the real one is, which is how
        /// an editor shows a layout made for another resolution in a corner of its own.
        /// </summary>
        public UIRect? Viewport { get; set; }

        /// <summary>
        /// Cuts everything in the module off at the edges of a rectangle of its own space, for a module that
        /// is shown inside of something (an editor's canvas) and mustn't spill out of it.
        /// </summary>
        public UIRect? Clip { get; set; }

        /// <summary>
        /// Whether the pointer reaches the module. Off, it is only looked at. Nothing in it hovers, presses or
        /// takes the focus.
        /// </summary>
        public bool Interactive { get; set; } = true;

        /// <summary>Whether the engine's layout debugger draws over the module and lists it.</summary>
        public bool ShowInLayoutDebugger { get; set; } = true;

        /// <summary>
        /// The component that has something open on top of everything else in the module (the list of a
        /// dropdown), null when nothing does. See <see cref="UIComponent.OpenPopup"/>.
        /// </summary>
        public UIComponent? Popup { get; internal set; }

        public UIModule(UICompositor compositor)
        {
            Compositor = compositor;
            Root.Module = this;

            SetupRuntime();
        }

        public T AddComponent<T>(T component) where T : UIComponent => Root.Add(component);

        /// <summary>
        /// Builds the UI a layout file describes into this module, see <see cref="UILayout"/>.
        /// </summary>
        /// <param name="parent">What the top of the layout is put inside of, null for the screen.</param>
        public UILayout LoadLayout(string path, UIComponent? parent = null) => UILayout.Load(this, path, parent);

        public bool RemoveComponent(UIComponent component) => Root.Remove(component);

        /// <summary>Turns a point in the camera's world space into the space the components are laid out in.</summary>
        public Vector2 ToLocal(Vector2 point) => (point - Position * UnitScale) / (Scale * UnitScale);

        /// <summary>Turns a point in the space the components are laid out in into the camera's world space.</summary>
        public Vector2 ToWorld(Vector2 point) => (point * Scale + Position) * UnitScale;

        internal void Update(float dt) => Root.UpdateTree(dt);

        /// <summary>
        /// Works out how big a unit of the layout is on this screen, before the pointer is routed. The pointer has
        /// to be turned into the module's units the same way the layout is drawn.
        /// </summary>
        internal void FitTo(UIRect screen)
        {
            // Shown by somebody else (an editor scales and places it itself), or not designed for a screen at all:
            // the compositor's scale it is
            if (Viewport is not null || DesignSize is not { X: > 0.0f, Y: > 0.0f } design || screen.IsEmpty)
            {
                UnitScale = Compositor.UIScale;
                return;
            }

            // Just fits, whichever way round the screen is, and then as big as the player likes their UI
            float fit = MathF.Min(screen.Width / design.X, screen.Height / design.Y);
            UnitScale = MathF.Max(0.01f, fit * Compositor.Scale);
        }

        internal void Layout(UIRect screen, UISkin skin)
        {
            Root.MeasureTree(skin);

            // The screen as the module sees it once its own scale and that of the whole UI are taken out,
            // so that scaled back up it covers the real screen exactly.
            Vector2 scale = Scale * UnitScale;
            UIRect seen = Viewport ?? new UIRect(screen.Min / scale, screen.Max / scale);

            // A contained design is its own size whatever the screen, smack in the middle of it. On an ultrawide
            // the sides are left alone instead of the menu being dragged out to the edges of the bloody thing
            Frame = DesignSize is { X: > 0.0f, Y: > 0.0f } design && Fit == UIFit.Contain
                ? UIRect.FromCenter(seen.Center, design)
                : seen;

            Root.ArrangeTree(Frame);
        }

        internal void Paint(UIDrawList list)
        {
            // Where a module is counts in units of the layout like everything in it, so it scales along.
            list.SetTransform(Position * UnitScale, Scale * UnitScale);

            if (Clip is { } clip)
                list.PushClip(clip);

            Root.PaintTree(list);

            // The edges of the layout, for whoever is editing it
            // and of the screen it's being shown on, fainter, when that's another shape than what it's made for
            if (FrameColor is { } frameColor)
            {
                float width = 2.0f / MathF.Max(0.01f, Scale.X);
                list.Outline(Frame, width, frameColor);

                if (Viewport is { } screen && screen != Frame)
                    list.Outline(screen, width * 0.5f, frameColor with { W = frameColor.W * 0.4f });
            }

            if (Clip is not null)
                list.PopClip();

            // Whatever is open goes on top of the lot, and isn't cut off by what its owner is inside of.
            if (Popup is { } popup)
            {
                if (popup.Module == this && popup.Visible)
                    popup.PaintPopup(list);
                else
                    Popup = null;
            }
        }

        internal UIComponent? HitTest(Vector2 point)
        {
            if (!Interactive)
                return null;

            Vector2 local = ToLocal(point);

            // What is open is on top, so it gets the pointer before anything under it does.
            if (Popup is { } popup && popup.PopupContains(local))
                return popup;

            return Root.HitTest(local);
        }

        /// <summary>
        /// The innermost component at a point of the camera's world space, whatever kind it is: unlike the
        /// pointer this finds labels and panels too, and doesn't care whether the module is interactive.
        /// </summary>
        /// <param name="hidden">Whether components that aren't visible are found as well.</param>
        public UIComponent? FindAt(Vector2 point, bool hidden = false) =>
            UILayoutOverlay.FindUnder(Root, ToLocal(point), hidden);
    }
}
