using System.Diagnostics.CodeAnalysis;

using Horizon.Core;

namespace Horizon.Engine;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public abstract class GameObject : Entity
{
    public static GameEngine Engine { get; internal set; }

    protected GameObject()
        :base()
    {
        
    }

    //public override void Initialize()
    //{
    //    if (Engine is null && Parent is GameEngine engine)
    //        Engine = engine;

    //    base.Initialize();
    //}
}