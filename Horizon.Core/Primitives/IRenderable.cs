namespace Horizon.Core.Primitives;

public interface IRenderable
{
    /// <summary>
    /// Draws the current object.
    /// </summary>
    /// <param name="dt">The elapsed time since the last update call.</param>
    public void Render(float dt, object? obj = null);
}