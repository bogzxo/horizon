#if DEBUG
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;

using Horizon.Core.Collections;
using Horizon.Core.Data;

using Egui;
using Egui.Containers;
using Egui.Widgets;

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

    public override void RenderUi(Ui root)
    {
        if (!Visible)
            return;

        new Window(Name)
            .Show(root.Ctx, ui =>
            {
                ui.Label($"FPS (Render): {1.0f / _renderDeltas.Buffer.Average():0.0}");
                ui.Label($"FPS (UpdateState): {1.0f / _stateDeltas.Buffer.Average():0.0}");

                ui.Collapsing("Logic Profiler", innerUi => 
                {
                    DrawCpuProfiling(innerUi);
                });

                ui.Collapsing("Render Profiler", innerUi =>
                {
                    DrawGpuProfling(innerUi);
                });
            });
    }

    private void DrawGpuProfling(Ui ui)
    {
        PlotValues(ui, "Frametime (GPU)", in _renderFrameTimes);
        DrawProfiler(ui, GpuMetrics);

        if (GpuMetrics.Categories["EngineComponents"].Keys.Any())
        {
            string[] names = GpuMetrics.Categories["EngineComponents"].Keys.ToArray();
            double[] values = GpuMetrics.Categories["EngineComponents"].Values
                .ToArray()
                .Select(r => GetAverage(r) * 1000.0)
                .ToArray();

            ui.Heading("GPU Breakdown");
            for (int i = 0; i < names.Length; i++)
            {
                ui.Label($"{names[i]}: {values[i]:0.00} ms");
            }
        }
    }

    private void DrawCpuProfiling(Ui ui)
    {
        PlotValues(ui, "Frametime (CPU)", in _updateFrameTimes);
        DrawProfiler(ui, CpuMetrics);
    }

    private void DrawProfiler(Ui ui, Metrika metrics)
    {
        foreach (var categoryEntry in metrics.Categories)
        {
            if (categoryEntry.Key.CompareTo("Engine") == 0)
                continue;

            ui.Heading(categoryEntry.Key);

            foreach (var valueEntry in categoryEntry.Value)
            {
                ui.Horizontal(row =>
                {
                    row.Label(valueEntry.Key);
                    row.Label((GetAverage(valueEntry.Value) * 1000000.0).ToString("0.00") + "us");
                });
            }
        }
    }

    [Pure]
    public static void PlotValues(
        Ui ui,
        in string label,
        in LinearBuffer<double> frameTimes,
        in string unit = "ms"
    )
    {
        var averageFrameTime = frameTimes.Buffer.Average();
        var minFrameTime = frameTimes.Buffer.Min();
        var maxFrameTime = frameTimes.Buffer.Max();

        ui.Label($"{label}: Avg {averageFrameTime:0.00}{unit} | Min {minFrameTime:0.00}{unit} | Max {maxFrameTime:0.00}{unit}");
    }

    [Pure]
    public static float GetMemoryUsage()
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
#endif