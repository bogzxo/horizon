using System.Numerics;
using Horizon.Rendering;

namespace Horizon.Core.Components;

/// <summary>
/// Where something is in a flat world, which way it is turned and how big it is.
/// <para>
/// The model matrix is only worked out when it is asked for after something changed, not on every change. Moving
/// something about a few times in an update costs nothing more than moving it once.
/// </para>
/// </summary>
public class TransformComponent2D : GameComponent
{
    private Vector2 pos;

    // In degrees
    private float rot;

    private Vector2 size = Vector2.One;
    private float zOffset;
    private Origin origin = Origin.Center;

    private Matrix4x4 modelMatrix;
    private volatile bool dirty = true;

    /// <summary>
    /// How often something has been put somewhere else rather than moved there, see <see cref="Snap"/>. Whatever draws
    /// the thing between two ticks doesn't show it on its way across a snap.
    /// </summary>
    public int Epoch { get; private set; }

    /// <summary>
    /// How far along Z it sits. For when two things at the same depth fight over who is in front.
    /// </summary>
    public float ZOffset
    {
        get => zOffset;
        set
        {
            zOffset = value;
            dirty = true;
        }
    }

    /// <summary>
    /// Which point of the thing its position is the position of, its middle unless said otherwise.
    /// </summary>
    public Origin Origin
    {
        get => origin;
        set
        {
            origin = value;
            dirty = true;
        }
    }

    private Vector2 GetOriginOffset()
    {
        // The quad everything is drawn as goes from -0.5 to 0.5 either way
        return origin switch
        {
            Origin.Center => new Vector2(0f, 0f),

            // Pinned by its right edge, so the rest of it is to the left of where it is
            Origin.Right => new Vector2(-0.5f, 0f),
            Origin.Left => new Vector2(0.5f, 0f),

            // Up is up, the world has its Y going up
            Origin.Top => new Vector2(0f, -0.5f),
            Origin.Bottom => new Vector2(0f, 0.5f),

            Origin.TopLeft => new Vector2(0.5f, -0.5f),
            Origin.TopRight => new Vector2(-0.5f, -0.5f),
            Origin.BottomLeft => new Vector2(0.5f, 0.5f),
            Origin.BottomRight => new Vector2(-0.5f, 0.5f),

            _ => Vector2.Zero
        };
    }

    /// <summary>
    /// Helper method to work the model matrix out from the position, rotation, size and origin as they are.
    /// Origin offset, then scale, then rotation, then the position in the world.
    /// </summary>
    private Matrix4x4 ComputeModelMatrix()
    {
        Vector2 originOffset = GetOriginOffset();
        float radians = float.DegreesToRadians(rot);
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);

        // The same as translate * scale * rotate * translate, written out. A 2D transform has six numbers worth working out, the other ten are along for the ride
        float ax = size.X * cos, ay = size.X * sin;
        float bx = -size.Y * sin, by = size.Y * cos;
        float ox = originOffset.X, oy = originOffset.Y;

        return new Matrix4x4(
            ax, ay, 0.0f, 0.0f,
            bx, by, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            ox * ax + oy * bx + pos.X, ox * ay + oy * by + pos.Y, zOffset, 1.0f);
    }

    /// <summary>
    /// The matrix that puts the unit quad where the thing is, turned and sized. Worked out the first time it is asked for after a change.
    /// </summary>
    public Matrix4x4 ModelMatrix
    {
        get
        {
            if (dirty)
            {
                dirty = false;
                modelMatrix = ComputeModelMatrix();
            }

            return modelMatrix;
        }
    }

    /// <summary>
    /// Where it is in the world.
    /// </summary>
    public Vector2 Position
    {
        get => pos;
        set
        {
            pos = value;
            dirty = true;
        }
    }

    /// <summary>
    /// Which way it is turned, in degrees, anticlockwise.
    /// </summary>
    public float Rotation
    {
        get => rot;
        set
        {
            rot = value;
            dirty = true;
        }
    }

    /// <summary>
    /// Puts the thing so that its top left corner is at a position, rather than its middle.
    /// </summary>
    public void SetPositionRelativeToOrigin(Vector2 position)
    {
        Position = position + size / new Vector2(2, -2);
    }

    /// <summary>
    /// How big it is, in units of the world.
    /// </summary>
    public Vector2 Size
    {
        get => size;
        set
        {
            size = value;
            dirty = true;
        }
    }

    /// <summary>
    /// Says that the thing was put where it is rather than moved there (a respawn, a reset), so it isn't drawn on its way
    /// from where it was in the frames that show the moment in between. Simulation thread.
    /// </summary>
    public void Snap() => Epoch++;

    /// <summary>
    /// Nothing to make on the GPU, the matrix is just worked out afresh the first time it is wanted.
    /// </summary>
    public override void Initialize()
    {
        dirty = true;
    }
}
