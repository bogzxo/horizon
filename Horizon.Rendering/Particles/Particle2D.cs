﻿using System.Numerics;

namespace Horizon.Rendering.Particles;

public record struct Particle2D(Vector2 Direction, Vector2 InitialPosition, float Speed = 32)
{
    public float Age;
    public float Random;
}
