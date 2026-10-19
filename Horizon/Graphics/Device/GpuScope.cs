namespace Horizon.Graphics;

/// <summary>
/// How long a stretch of a frame took on the GPU, see <see cref="GraphicsDevice.BeginGpuScope"/>. A scope inside of
/// another has a depth one more than it.
/// </summary>
public readonly record struct GpuScope(string Name, double Milliseconds, int Depth);

/// <summary>
/// An open scope, which ends when it is disposed of, so the usual way is
/// <code>
/// using (device.BeginGpuScope("lights"))
/// {
///     // draws and dispatches
/// }
/// </code>
/// </summary>
public readonly struct GpuScopeToken : IDisposable
{
    private readonly GraphicsDevice? device;

    internal GpuScopeToken(GraphicsDevice? device)
    {
        this.device = device;
    }

    public void Dispose() => device?.EndGpuScope();
}
