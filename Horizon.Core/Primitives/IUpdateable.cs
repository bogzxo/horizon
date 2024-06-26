﻿namespace Horizon.Core.Primitives;

public interface IUpdateable
{
    /// <summary>
    /// Updates the current object's state.
    /// </summary>
    /// <param name="dt">The elapsed time since the last update call.</param>
    public void UpdateState(float dt);

    /// <summary>
    /// Updates the current object's physics.
    /// </summary>
    /// <param name="dt">The elapsed time since the last update call.</param>
    public void UpdatePhysics(float dt);
}

public interface IRenderable
{
    /// <summary>
    /// Draws the current object.
    /// </summary>
    /// <param name="dt">The elapsed time since the last update call.</param>
    public void Render(float dt, object? obj = null);
}
