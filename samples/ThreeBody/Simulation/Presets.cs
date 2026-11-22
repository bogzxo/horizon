using System.Numerics;

namespace ThreeBody.Simulation;

public enum Preset { FigureEight, Lagrange, Chaotic, SunPlanetMoon, Pythagorean, BinaryPlanet, Butterfly, Sitnikov }

/// <summary>Initial conditions for each preset, G = 1.</summary>
public static class Presets
{
    private static readonly Vector4 Red = new(1.0f, 0.35f, 0.3f, 1f);
    private static readonly Vector4 Green = new(0.4f, 1.0f, 0.5f, 1f);
    private static readonly Vector4 Blue = new(0.4f, 0.6f, 1.0f, 1f);
    private static readonly Vector4 Yellow = new(1.0f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Grey = new(0.8f, 0.8f, 0.85f, 1f);

    public static Body[] Create(Preset preset, Random rng) => preset switch
    {
        Preset.FigureEight => FigureEight(),
        Preset.Lagrange => Lagrange(),
        Preset.Chaotic => Chaotic(rng),
        Preset.SunPlanetMoon => SunPlanetMoon(),
        Preset.Pythagorean => Pythagorean(),
        Preset.BinaryPlanet => BinaryPlanet(),
        Preset.Butterfly => Butterfly(),
        Preset.Sitnikov => Sitnikov(),
        _ => FigureEight(),
    };

    // Sitnikov-style setup: two equal masses on an eccentric mutual orbit (e = 0.5), started at apocentre
    // with separation 1, plus a light third body at rest on the axis through their centre of mass.
    // The simulation is 2D, so this is the in-plane analogue of the Sitnikov problem seen edge-on
    // (in the true problem the third body moves out of the primaries' orbital plane).
    private static Body[] Sitnikov()
    {
        const double e = 0.5, sep = 1.0, m = 0.5;
        double vRel = Math.Sqrt(2 * m * (1 - e) / sep);   // relative speed at apocentre, G = 1
        double v = vRel / 2;                              // equal masses share it, opposite directions
        return
        [
            new Body { Name = "A", Mass = m, X = sep / 2, VY = v, Color = Yellow, Radius = 0.06 },
            new Body { Name = "B", Mass = m, X = -sep / 2, VY = -v, Color = Red, Radius = 0.06 },
            new Body { Name = "C", Mass = 0.001, X = 0, Y = 1.0, Color = Blue, Radius = 0.03 },
        ];
    }

    // Burrau's problem: masses 3, 4, 5 at rest on the corners of a 3-4-5 right triangle, opposite their sides
    private static Body[] Pythagorean() =>
    [
        new Body { Name = "3", Mass = 3, X = 1, Y = 3, Color = Red, Radius = 0.06 },
        new Body { Name = "4", Mass = 4, X = -2, Y = -1, Color = Green, Radius = 0.07 },
        new Body { Name = "5", Mass = 5, X = 1, Y = -1, Color = Blue, Radius = 0.08 },
    ];

    // Two equal stars on a circular orbit (v^2 = G m / 2d, d = 1) with a light planet circling both at r = 4
    private static Body[] BinaryPlanet()
    {
        double vs = Math.Sqrt(1.0 / 2.0);                 // each star, m = 1, separation 1
        const double rp = 6.0;                            // well outside the stable limit of about 3 binary separations
        double vp = Math.Sqrt(2.0 / rp);                  // circular speed about the pair's total mass (G = 1, M = 2)
        return
        [
            new Body { Name = "Star A", Mass = 1, X = 0.5, VY = vs, Color = Yellow, Radius = 0.06 },
            new Body { Name = "Star B", Mass = 1, X = -0.5, VY = -vs, Color = Red, Radius = 0.06 },
            new Body { Name = "Planet", Mass = 0.001, X = rp, VY = vp, Color = Blue, Radius = 0.03 },
        ];
    }

    // Suvakov-Dmitrasinovic "butterfly I" periodic orbit, period about 6.2356
    private static Body[] Butterfly()
    {
        const double vx = 0.30689, vy = 0.12551;
        return
        [
            new Body { Name = "A", Mass = 1, X = -1, VX = vx, VY = vy, Color = Red, Radius = 0.04 },
            new Body { Name = "B", Mass = 1, X = 1, VX = vx, VY = vy, Color = Green, Radius = 0.04 },
            new Body { Name = "C", Mass = 1, X = 0, VX = -2 * vx, VY = -2 * vy, Color = Blue, Radius = 0.04 },
        ];
    }

    // Chenciner-Montgomery, Moore's initial conditions, period about 6.3259
    private static Body[] FigureEight()
    {
        const double x = 0.97000436, y = -0.24308753, vx = 0.4662036850, vy = 0.4323657300;
        return
        [
            new Body { Name = "A", Mass = 1, X = x, Y = y, VX = vx, VY = vy, Color = Red, Radius = 0.04 },
            new Body { Name = "B", Mass = 1, X = -x, Y = -y, VX = vx, VY = vy, Color = Green, Radius = 0.04 },
            new Body { Name = "C", Mass = 1, X = 0, Y = 0, VX = -2 * vx, VY = -2 * vy, Color = Blue, Radius = 0.04 },
        ];
    }

    // Equilateral triangle rotating rigidly about the centre of mass
    private static Body[] Lagrange()
    {
        const double side = 2.0, m = 1.0;
        double r = side / Math.Sqrt(3.0);              // centre to vertex
        double omega = Math.Sqrt(3.0 * m / (side * side * side)); // omega^2 = G*M_total/side^3
        var bodies = new Body[3];
        Vector4[] cols = [Red, Green, Blue];
        for (int i = 0; i < 3; i++)
        {
            double a = i * 2.0 * Math.PI / 3.0 + Math.PI / 2;
            bodies[i] = new Body
            {
                Name = ((char)('A' + i)).ToString(), Mass = m, Color = cols[i], Radius = 0.06,
                X = r * Math.Cos(a), Y = r * Math.Sin(a),
                VX = -omega * r * Math.Sin(a), VY = omega * r * Math.Cos(a),
            };
        }
        return bodies;
    }

    private static Body[] Chaotic(Random rng)
    {
        var bodies = new Body[3];
        Vector4[] cols = [Red, Green, Blue];
        double px = 0, py = 0, mt = 0;
        for (int i = 0; i < 3; i++)
        {
            double m = 0.8 + rng.NextDouble() * 0.8;
            double a = rng.NextDouble() * Math.PI * 2, d = 0.6 + rng.NextDouble() * 1.2;
            double va = rng.NextDouble() * Math.PI * 2, sp = 0.2 + rng.NextDouble() * 0.5;
            bodies[i] = new Body
            {
                Name = ((char)('A' + i)).ToString(), Mass = m, Color = cols[i], Radius = 0.035 + 0.025 * m,
                X = d * Math.Cos(a), Y = d * Math.Sin(a), VX = sp * Math.Cos(va), VY = sp * Math.Sin(va),
            };
            px += m * bodies[i].VX; py += m * bodies[i].VY; mt += m;
        }
        // Remove net momentum so the system does not drift away
        foreach (var b in bodies) { b.VX -= px / mt; b.VY -= py / mt; }
        return bodies;
    }

    // Heavy sun, light planet in a circular orbit, tiny moon around the planet
    private static Body[] SunPlanetMoon()
    {
        const double ms = 100.0, mp = 1.0, mm = 0.01;
        const double rp = 5.0, rm = 0.4;
        double vp = Math.Sqrt((ms + mp + mm) / rp);       // planet about the sun (approx)
        double vm = Math.Sqrt(mp / rm);                   // moon about the planet
        // Place sun so the centre of mass is near origin, then zero momentum
        var sun = new Body { Name = "Sun", Mass = ms, Color = Yellow, Radius = 0.25 };
        var planet = new Body { Name = "Planet", Mass = mp, X = rp, VY = vp, Color = Blue, Radius = 0.1 };
        var moon = new Body { Name = "Moon", Mass = mm, X = rp + rm, VY = vp + vm, Color = Grey, Radius = 0.04 };
        double mt = ms + mp + mm;
        double cx = (planet.Mass * planet.X + moon.Mass * moon.X) / mt;
        double cvy = (planet.Mass * planet.VY + moon.Mass * moon.VY) / mt;
        foreach (var b in new[] { sun, planet, moon }) { b.X -= cx; b.VY -= cvy; }
        return [sun, planet, moon];
    }
}
