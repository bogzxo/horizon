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

        public IReadOnlyList<UIComponent> Components => Root.Children;

        /// <summary>Moves the whole module, in the world space of the compositor's camera.</summary>
        public Vector2 Position { get; set; }

        /// <summary>
        /// Scales the whole module. Components keep their places relative to the screen: one anchored
        /// to a corner stays in that corner and simply gets bigger.
        /// </summary>
        public Vector2 Scale { get; set; } = Vector2.One;

        public UIModule(UICompositor compositor)
        {
            Compositor = compositor;
            Root.Module = this;

            SetupRuntime();
        }

        public T AddComponent<T>(T component) where T : UIComponent => Root.Add(component);

        public bool RemoveComponent(UIComponent component) => Root.Remove(component);

        /// <summary>Turns a point in the camera's world space into the space the components are laid out in.</summary>
        public Vector2 ToLocal(Vector2 point) => (point - Position) / Scale;

        /// <summary>Turns a point in the space the components are laid out in into the camera's world space.</summary>
        public Vector2 ToWorld(Vector2 point) => point * Scale + Position;

        internal void Update(float dt) => Root.UpdateTree(dt);

        internal void Layout(UIRect screen, UISkin skin)
        {
            Root.MeasureTree(skin);

            // The screen as the module sees it once its own scale is taken out, so that scaled back
            // up it covers the real screen exactly.
            Root.ArrangeTree(new UIRect(screen.Min / Scale, screen.Max / Scale));
        }

        internal void Paint(UIDrawList list)
        {
            list.SetTransform(Position, Scale);
            Root.PaintTree(list);
        }

        internal UIComponent? HitTest(Vector2 point) => Root.HitTest(ToLocal(point));
    }
}
