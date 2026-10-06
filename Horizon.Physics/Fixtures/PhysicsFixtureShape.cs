using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Text;

namespace Horizon.Physics.Fixtures;

public enum PhysicsFixtureShape
{
    Rectangle,
    Circle,

    // Any shape at all, as the lines around it. Only particles collide with these, see OutlinePhysicsFixture
    Outline
}
