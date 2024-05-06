using Horizon.Core;

namespace Horizon.Engine;

public abstract class GameObject : Entity
{
    public static GameEngine Engine { get; internal set; }

    //public override void Initialize()
    //{
    //    if (Engine is null && Parent is GameEngine engine)
    //        Engine = engine;

    //    base.Initialize();
    //}
}