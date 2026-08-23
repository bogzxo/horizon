using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.UIX.Components
{
    public interface IUIComponent
    {
        public ObjectValue Object { get; init; }
        void Initialize(UICompositor compositor);
        void UpdateState(float dt);
    }
}
