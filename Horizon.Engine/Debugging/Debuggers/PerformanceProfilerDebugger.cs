using System.Diagnostics.Contracts;
using System.Numerics;

using Horizon.Core.Collections;
using Horizon.Core.Data;

using ImGuiNET;

using ImPlotNET;

namespace Horizon.Engine.Debugging.Debuggers;

public class PerformanceProfilerDebugger : DebuggerComponent, IDisposable
{
    /// <summary>
    /// How many times/s metrics are collected.
    /// </summary>
    public int UpdateRate
    {
        get => (int)(1 / _updateRate);
        set => _updateRate = 1.0f / value;
    }

    public double LogicRate { get => (GetAverage(_stateDeltas)); }
    public double PhysicsRate { get => (GetAverage(_physicsDeltas)); }
    public double RenderRate { get => (GetAverage(_renderDeltas)); }

    private float _updateRate = 1.0f / 30.0f;

    private float _updateTimer,
        _renderTimer, _physicsTimer;

    private SkylineDebugger Debugger { get; set; }

    public readonly Metrika CpuMetrics = new();
    public readonly Metrika GpuMetrics = new();

    private LinearBuffer<double> _updateFrameTimes,
        _renderFrameTimes;

    private LinearBuffer<double> _stateDeltas, _physicsDeltas,
        _renderDeltas;

    private long _prevTimestamp;
    private long _prevCpuTime;
    private bool disposedValue;

    public override void Initialize()
    {
        Name = "Profiler";

        Debugger = (Parent as SkylineDebugger)!;

        int collectionSize = 25;

        _updateFrameTimes = new(collectionSize);
        _renderFrameTimes = new(collectionSize);

        _stateDeltas = new(collectionSize);
        _renderDeltas = new(collectionSize);
        _physicsDeltas = new(collectionSize);

        // Initialize requried dictionaries by inference.
        CpuMetrics.AddCustom("Engine", "CPU", 0.0);
        CpuMetrics.AddCustom("Engine", "State", 0.0);
        CpuMetrics.AddCustom("Engine", "Physics", 0.0);

        // GPU
        GpuMetrics.AddCustom("Engine", "GPU", 0.0);

        //// Spritebatches (for 2d)
        CpuMetrics.CreateCategory("EngineComponents");
        GpuMetrics.CreateCategory("EngineComponents");

        GameEngine.Instance.EventManager.PreRender += ResetGpuMetrics;
        GameEngine.Instance.EventManager.PreState += ResetStateMetrics;
        GameEngine.Instance.EventManager.PrePhysics += ResetPhysicsMetrics;

        GameEngine.Instance.EventManager.PostState += UpdateUpdateMetrics;
        GameEngine.Instance.EventManager.PostPhysics += UpdatePhysicsMetrics;
        GameEngine.Instance.EventManager.PostRender += UpdateRenderMetrics;
    }

    private void ResetGpuMetrics(float dt)
    {
        //GpuMetrics.ResetMetrics();
    }

    private void ResetStateMetrics(float dt)
    {
        //CpuMetrics.ResetMetrics();
    }

    private void ResetPhysicsMetrics(float dt)
    {
        //CpuMetrics.ResetMetrics();
    }

    private void UpdatePhysicsMetrics(float dt)
    {
        if (!Enabled)
            return;

        _physicsTimer += dt;

        if (_physicsTimer > _updateRate)
        {
            _physicsTimer = 0.0f;
            _physicsDeltas.Append(dt);
        }
    }

    private void UpdateUpdateMetrics(float dt)
    {
        if (!Enabled)
            return;

        _updateTimer += dt;

        if (_updateTimer > _updateRate)
        {
            _updateTimer = 0.0f;
            _stateDeltas.Append(dt);
            _updateFrameTimes.Append(GetAverage(CpuMetrics["Engine"]["CPU"]) * 1000.0);
        }
    }

    private void UpdateRenderMetrics(float dt)
    {
        if (!Enabled)
            return;

        _renderTimer += dt;

        if (_renderTimer > _updateRate)
        {
            _renderTimer = 0.0f;
            _renderDeltas.Append(dt);
            _renderFrameTimes.Append(GetAverage(GpuMetrics["Engine"]["GPU"]) * 1000.0);
        }
    }

    private double GetAverage(LinearBuffer<double> linearBuffer) => linearBuffer.Buffer.Average();

    public override void Render(float dt, object? obj = null)
    {
        if (!Visible)
            return;

        if (ImGui.Begin(Name))
        {
            ImGui.Text($"FPS (Render): {1.0f / _renderDeltas.Buffer.Average():0.0}");
            ImGui.Text($"FPS (UpdateState): {1.0f / _stateDeltas.Buffer.Average():0.0}");

            if (ImGui.CollapsingHeader("Logic Profiler"))
                DrawCpuProfiling();
            if (ImGui.CollapsingHeader("Render Profiler"))
                DrawGpuProfling();

            ImGui.End();
        }
    }

    private void DrawGpuProfling()
    {
        PlotValues("Frametime (GPU)", in _renderFrameTimes);
        DrawProfiler(GpuMetrics);

        if (
            GpuMetrics.Categories["EngineComponents"].Keys.Any()
            && ImPlot.BeginPlot(
                "Test",
                new Vector2(ImGui.GetContentRegionAvail().X, 200.0f),
                ImPlotFlags.Equal
            )
        )
        {
            string[] names = GpuMetrics.Categories["EngineComponents"].Keys.ToArray();
            double[] values = GpuMetrics.Categories["EngineComponents"].Values
                .ToArray()
                .Select(
                    (r) =>
                    {
                        return GetAverage(r) * 1000.0;
                    }
                )
                .ToArray();

            ImPlot.SetupAxes(null, null, ImPlotAxisFlags.AutoFit, ImPlotAxisFlags.AutoFit);
            ImPlot.PlotPieChart(
                names,
                ref values[0],
                names.Length,
                0.0,
                0.0,
                1.0,
                "",
                0.0,
                ImPlotPieChartFlags.Normalize
            );

            ImPlot.EndPlot();
        }
    }

    private void DrawCpuProfiling()
    {
        PlotValues("Frametime (CPU)", in _updateFrameTimes);
        DrawProfiler(CpuMetrics);
    }

    private void DrawProfiler(Metrika gpuMetrics)
    {
        foreach (var categoryEntry in gpuMetrics.Categories)
        {
            if (categoryEntry.Key.CompareTo("Engine") == 0)
                continue;

            ImGui.Text(categoryEntry.Key);

            foreach (var valueEntry in categoryEntry.Value)
            {
                ImGui.Columns(2, "ProfilerTimerValueColumns", true);

                ImGui.Text(valueEntry.Key);
                ImGui.NextColumn();
                ImGui.Text((GetAverage(valueEntry.Value) * 1000000.0).ToString("0.00") + "us");

                ImGui.Columns(1);
            }
        }
    }

    [Pure]
    private static void PlotValues(
        in string label,
        in LinearBuffer<double> frameTimes,
        in string unit = "ms"
    )
    {
        var windowWidth = ImGui.GetContentRegionAvail().X;
        var averageFrameTime = frameTimes.Buffer.Average();
        var minFrameTime = frameTimes.Buffer.Min();
        var maxFrameTime = frameTimes.Buffer.Max();

        ImPlot.SetNextAxisLimits(ImAxis.X1, 0, frameTimes.Length);
        ImPlot.SetNextAxisLimits(ImAxis.Y1, minFrameTime, maxFrameTime * 1.2);

        if (
            ImPlot.BeginPlot(
                $"{label} - Avg: {averageFrameTime:0.00}{unit} - Max Diff: {(maxFrameTime - minFrameTime):0.00}{unit} - Excp. FPS: {1.0f / (averageFrameTime / 1000.0f):0}FPS",
                new Vector2(windowWidth, 200.0f)
            )
        )
        {
            ImPlot.PlotLine(
                "",
                ref frameTimes.Buffer[0],
                frameTimes.Length,
                1.0f,
                0.0,
                ImPlotLineFlags.Shaded,
                frameTimes.Index
            );

            ImPlot.EndPlot();
        }
    }

    [Pure]
    public float GetMemoryUsage()
    {
        return (float)(GC.GetTotalMemory(false) / (1024.0 * 1024.0)); // in MB
    }

    public override void UpdateState(float dt)
    { }

    public override void UpdatePhysics(float dt)
    { }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // We subscribed to engine events, so we need to make sure to clean 'em up.

                GameEngine.Instance.EventManager.PreRender -= ResetGpuMetrics;
                GameEngine.Instance.EventManager.PreState -= ResetStateMetrics;

                GameEngine.Instance.EventManager.PrePhysics -= ResetPhysicsMetrics;
                GameEngine.Instance.EventManager.PostPhysics -= UpdatePhysics;

                GameEngine.Instance.EventManager.PostState -= UpdateUpdateMetrics;
                GameEngine.Instance.EventManager.PostRender -= UpdateRenderMetrics;
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~PerformanceProfilerDebugger()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public override void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}