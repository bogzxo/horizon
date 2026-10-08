namespace Horizon.Core.Primitives;

/// <summary>
/// The most primitive type, represents any handle bound object.
/// </summary>
public interface IGLObject
{
    public uint Handle { get; init; }
}

/// <summary>
/// Abstraction around <see cref="IGLObject"/> for the objects that talk to the GL themselves. The GL is the one of the
/// window, see <see cref="Horizon.OpenGL.Managers.ObjectManager.GL"/>: there is only ever the one, on the one thread that draws.
/// </summary>
public abstract class GLObject : IGLObject
{
    protected static Silk.NET.OpenGL.GL GL => Horizon.OpenGL.Managers.ObjectManager.GL;

    public uint Handle { get; init; }
}
