using System.Numerics;

namespace Horizon.Physics.Fixtures;

public interface IPhysicsFixture
{
    public string Tag { get; init; }
    public PhysicsFixtureShape Shape { get; init; }
    public bool TestIntersection(in IPhysicsFixture other, Vector2 positionOffset, Vector2 otherPositionOffset);
    public bool IsTouching { get; internal set; }
    public HashSet<IPhysicsFixture> ActiveContacts { get; }
}
