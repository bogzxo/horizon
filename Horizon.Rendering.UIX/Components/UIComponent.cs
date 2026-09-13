using System.Numerics;
using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.UIX.Components
{
    public abstract class UIComponent
    {
        public ObjectValue Object { get; protected set; }
        public abstract void UpdateState(float dt, in UICompositor compositor, Vector2 mousePos, bool clicked);
        public abstract void Initialize(UICompositor compositor);
    }
}
