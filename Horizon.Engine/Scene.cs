namespace Horizon.Engine;

public abstract class Scene : GameObject
{
    public abstract Camera ActiveCamera { get; protected set; }
}