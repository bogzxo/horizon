using System.Numerics;

namespace ThreeBody.Simulation;

/// <summary>One gravitating body. Units are G = 1.</summary>
public sealed class Body
{
    public string Name = "";
    public double Mass = 1.0;
    public double X, Y;
    public double VX, VY;
    public Vector4 Color = Vector4.One;

    /// <summary>Suggested draw radius in world units, visual only.</summary>
    public double Radius = 0.05;
}
