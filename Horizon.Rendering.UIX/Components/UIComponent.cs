using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.UIX.Components
{
    public abstract class UIComponent
    {
        public ObjectValue Object { get; protected set; }
        public abstract void UpdateState(float dt);
        public abstract void Initialize(UICompositor compositor);
    }
}
