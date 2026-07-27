using System.Diagnostics.CodeAnalysis;

namespace Horizon.Engine;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public abstract class Scene : GameObject
{
    public abstract Camera ActiveCamera { get; protected set; }
}