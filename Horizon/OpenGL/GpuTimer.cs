using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL;

/// <summary>
/// Measures how long the GPU takes over what is drawn between <see cref="Begin"/> and <see cref="End"/>, with timer
/// queries. The GPU runs behind the CPU, so the result of a frame is read a few frames later, when it is there to be
/// read without waiting: <see cref="LastMilliseconds"/> is the newest frame that has finished. For the performance
/// overlay and anybody else who wants to know whether a frame is slow on the GPU or the CPU.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    // How many frames are in flight at the most, which is how many queries take turns
    private const int FRAMES = 4;

    private readonly QueryObject[] queries;
    private readonly bool[] pending = new bool[FRAMES];
    private int frame;

    /// <summary>How long (in milliseconds) the GPU spent on the newest frame it has finished.</summary>
    public double LastMilliseconds { get; private set; }

    private GpuTimer(QueryObject[] queries)
    {
        this.queries = queries;
    }

    /// <summary>
    /// Makes a timer, null if the GPU won't give out timer queries (which has been logged). GL thread.
    /// </summary>
    public static GpuTimer? TryCreate()
    {
        // The engine's, not the scene's that happened to be on
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        var queries = new QueryObject[FRAMES];
        for (int i = 0; i < FRAMES; i++)
        {
            if (!ObjectManager.Instance.Queries.TryCreate(QueryObjectDescription.Default, out var result))
                return null;

            queries[i] = result.Asset;
        }

        return new GpuTimer(queries);
    }

    // Whether a query is running right now, between Begin and End
    private bool timing;

    /// <summary>Starts timing a frame. Reads the result of the oldest frame that is done first.</summary>
    public void Begin()
    {
        if (timing) return;

        QueryObject query = queries[frame];

        // The query of this slot was started FRAMES frames ago, which is long enough for the GPU to be done with it
        if (pending[frame])
        {
            if (query.GetParameter(QueryObjectParameterName.ResultAvailable) == 0)
            {
                // Still going: this frame goes untimed rather than stalling on it
                return;
            }

            LastMilliseconds = query.GetParameter(QueryObjectParameterName.Result) / 1_000_000.0;
            pending[frame] = false;
        }

        query.Begin();
        pending[frame] = true;
        timing = true;
    }

    /// <summary>Ends the frame started by <see cref="Begin"/>.</summary>
    public void End()
    {
        if (!timing) return;

        queries[frame].End();
        frame = (frame + 1) % FRAMES;
        timing = false;
    }

    public void Dispose()
    {
        foreach (QueryObject query in queries)
            ObjectManager.Instance.Queries.Remove(query);
    }
}
