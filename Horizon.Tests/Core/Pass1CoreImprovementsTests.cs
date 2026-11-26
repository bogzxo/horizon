using System.Diagnostics;
using System.Numerics;
using System.Reflection;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Diagnostics;
using Horizon.Core.Threading;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Engine.Components;
using Horizon.Logging;
using Horizon.Logging.Loggers;

namespace Horizon.Tests.Core;

public class Pass1CoreImprovementsTests
{
    private sealed class TestEntity : Entity
    {
        public int DisposeCount { get; private set; }
        public Action? OnUpdateState { get; set; }

        public override void UpdateState(float dt)
        {
            OnUpdateState?.Invoke();
            base.UpdateState(dt);
        }

        protected override void DisposeOther()
        {
            DisposeCount++;
        }
    }

    private sealed class TestComponent : GameComponent;

    private sealed class TestScene : Scene
    {
        public int DisposeCount { get; private set; }

        protected override void DisposeOther()
        {
            DisposeCount++;
        }
    }

    [Fact]
    public async Task Deduplicated_typed_work_delivers_shared_result_to_all_callers()
    {
        var scheduler = new WorkScheduler();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstResult = null;
        string? secondResult = null;

        WorkItem first = scheduler.Start("fetch:version", "Fetching", _ => gate.Task, (_, value) => firstResult = value);
        WorkItem second = scheduler.Start("fetch:version", "Fetching again", _ => Task.FromResult("unused"), (_, value) => secondResult = value);

        Assert.Same(first, second);

        gate.SetResult("1.2.3");
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.Pump();

        Assert.Equal("1.2.3", firstResult);
        Assert.Equal("1.2.3", secondResult);
    }

    [Fact]
    public async Task Deduplicated_typed_work_delivers_default_when_cancelled_or_failed()
    {
        var scheduler = new WorkScheduler();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstResult = -1;
        int secondResult = -1;

        WorkItem first = scheduler.Start("compute:fail", "Computing", _ => gate.Task, (_, value) => firstResult = value);
        WorkItem second = scheduler.Start("compute:fail", "Computing again", _ => Task.FromResult(99), (_, value) => secondResult = value);

        Assert.Same(first, second);

        gate.SetException(new InvalidOperationException("boom"));
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.Pump();

        Assert.Equal(WorkState.Failed, first.State);
        Assert.Equal(0, firstResult);
        Assert.Equal(0, secondResult);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Interval_runner_substep_guards_non_positive_or_non_finite_interval(float interval)
    {
        int calls = 0;
        var runner = new IntervalRunnerSubStep(interval, () => calls++) { Enabled = true };

        runner.UpdateState(0.5f);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Interval_runner_substep_still_steps_for_positive_interval()
    {
        int calls = 0;
        var runner = new IntervalRunnerSubStep(0.1f, () => calls++) { Enabled = true };

        runner.UpdateState(0.35f);

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Entity_rejects_AddEntity_and_AddComponent_after_dispose()
    {
        var parent = new TestEntity();
        parent.Dispose();

        Assert.Throws<ObjectDisposedException>(() => parent.AddEntity(new TestEntity()));
        Assert.Throws<ObjectDisposedException>(() => parent.AddComponent(new TestComponent()));
    }

    [Fact]
    public void Entity_rejects_adding_already_disposed_child()
    {
        using var parent = new TestEntity();
        var disposedChild = new TestEntity();
        disposedChild.Dispose();

        Assert.Throws<ObjectDisposedException>(() => parent.AddEntity(disposedChild));
    }

    [Fact]
    public void Entity_allocation_tracking_stays_balanced_when_child_throws()
    {
        using var allocationLog = new AllocationLog();
        using var parent = new TestEntity { Enabled = true };
        var throwingChild = new TestEntity
        {
            Enabled = true,
            OnUpdateState = () => throw new InvalidOperationException("expected test failure")
        };

        parent.AddEntity(throwingChild);
        parent.InitializeAll();

        Assert.Throws<InvalidOperationException>(() => parent.UpdateState(0.016f));

        // Subsequent Begin/End on the same thread must still attribute cleanly without throwing or underflowing
        long before = AllocationLog.Begin(out long nested);
        byte[] work = new byte[4096];
        AllocationLog.End(parent, before, nested);
        GC.KeepAlive(work);
    }

    [Fact]
    public void SceneManager_dispose_cleans_up_active_incoming_preloaded_and_retired_scenes_once()
    {
        var manager = new SceneManager();
        var active = new TestScene();
        var retired = new TestScene();
        var incoming = new TestScene();
        var preloaded = new TestScene();
        var alreadyDisposed = new TestScene();
        alreadyDisposed.Dispose();

        typeof(SceneManager).GetProperty(nameof(SceneManager.CurrentInstance))!.SetValue(manager, active);
        manager.SetScene(retired);
        manager.SetScene(incoming);
        manager.Preload(preloaded);

        manager.Dispose();
        manager.Dispose();

        Assert.True(active.IsDisposed);
        Assert.True(retired.IsDisposed);
        Assert.True(incoming.IsDisposed);
        Assert.True(preloaded.IsDisposed);
        Assert.Equal(1, active.DisposeCount);
        Assert.Equal(1, retired.DisposeCount);
        Assert.Equal(1, incoming.DisposeCount);
        Assert.Equal(1, preloaded.DisposeCount);
        Assert.Equal(1, alreadyDisposed.DisposeCount);
        Assert.Null(manager.CurrentInstance);
    }

    [Fact]
    public void SceneManager_clears_IsLeaving_when_reselecting_current_scene_before_change_runs()
    {
        using var manager = new SceneManager();
        var current = new TestScene();
        var next = new TestScene();

        typeof(SceneManager).GetProperty(nameof(SceneManager.CurrentInstance))!.SetValue(manager, current);

        manager.SetScene(next);
        Assert.True(current.IsLeaving);

        manager.SetScene(current);
        Assert.False(current.IsLeaving);
    }

    [Fact]
    public void WindowManager_IsHidden_is_true_when_dormant_even_if_not_minimized()
    {
        long now = Stopwatch.GetTimestamp();

        Assert.True(WindowManager.IsHidden(RunMode.Application, isDormant: true, minimized: false, now, now));
        Assert.False(WindowManager.IsHidden(RunMode.Application, isDormant: false, minimized: false, now, now));
        Assert.False(WindowManager.IsHidden(RunMode.Game, isDormant: true, minimized: true, now, 0));
    }

    [Fact]
    public void TweenContext_Kill_handles_repeated_and_reentrant_kills_safely()
    {
        var context = new TweenContext();
        float a = 0.0f, b = 0.0f;
        int killCount = 0;

        Tween first = Tween.To(() => a, v => a = v, 10.0f, 1.0f).OnKill(() =>
        {
            killCount++;
            context.Kill("channel-a");
        });
        Tween second = Tween.To(() => b, v => b = v, 20.0f, 1.0f).OnKill(() => killCount++);

        context.Play(first, "channel-a");
        context.Play(second, "channel-b");

        context.Kill("channel-a");
        context.Kill("channel-a");
        context.Kill("channel-b");

        Assert.True(first.IsKilled);
        Assert.True(second.IsKilled);
        Assert.Equal(2, killCount);
    }

    [Fact]
    public void GameEngine_FlipRowsInPlace_flips_rows_without_allocating_second_frame_and_guards_empty()
    {
        byte[] empty = [];
        GameEngine.FlipRowsInPlace(empty, 0, 0);

        // 2x3 RGBA8 image (3 rows of 8 bytes each)
        byte[] pixels =
        [
            1, 2, 3, 4, 5, 6, 7, 8,
            9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24
        ];

        GameEngine.FlipRowsInPlace(pixels, 2, 3);

        Assert.Equal(
        [
            17, 18, 19, 20, 21, 22, 23, 24,
            9, 10, 11, 12, 13, 14, 15, 16,
            1, 2, 3, 4, 5, 6, 7, 8
        ], pixels);
    }

    [Fact]
    public void Camera2D_guards_zero_viewport_against_NaN_or_Infinity()
    {
        var camera = new Camera2D(Vector2.Zero);

        Assert.False(float.IsNaN(camera.Projection.M11) || float.IsInfinity(camera.Projection.M11));
        Assert.False(float.IsNaN(camera.Projection.M22) || float.IsInfinity(camera.Projection.M22));
        Assert.False(float.IsNaN(camera.ViewProj.M11) || float.IsInfinity(camera.ViewProj.M11));
    }

    [Fact]
    public void ConcurrentLogger_flushes_all_messages_and_handles_concurrent_dispose_cleanly()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"horizon-pass1-log-{Guid.NewGuid():N}.log");
        try
        {
            using (var logger = new ConcurrentLogger(logPath))
            {
                for (int i = 0; i < 20; i++)
                    logger.Log(LogLevel.Info, $"line-{i}");

                logger.Flush();

                // Concurrent logging while disposing must not throw
                Parallel.For(0, 16, i =>
                {
                    if (i == 8)
                        logger.Dispose();
                    else
                        logger.Log(LogLevel.Warning, $"race-{i}");
                });
            }

            string written = File.ReadAllText(logPath);
            for (int i = 0; i < 20; i++)
                Assert.Contains($"[Info] line-{i}", written);
        }
        finally
        {
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }
}
