using System.Numerics;

namespace Horizon.Physics.Fixtures;

public interface IPhysicsFixture
{
    public string Tag { get; init; }
    public PhysicsFixtureShape Shape { get; init; }
    public bool TestIntersection(in IPhysicsFixture other, Vector2 positionOffset, Vector2 otherPositionOffset);
    public bool IsTouching { get; internal set; }
    public HashSet<IPhysicsFixture> ActiveContacts { get; }

    /// <summary>
    /// What colour the debug view (<see cref="PhysicsWorld.RenderDebug"/>) outlines it in, null for the colour of the kind
    /// of body it's on. For telling one fixture from the rest, a fighting game's hitbox from its hurtbox.
    /// </summary>
    public Vector3? DebugColour { get; set; }
}
