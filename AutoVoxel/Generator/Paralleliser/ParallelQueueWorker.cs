using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using AutoVoxel.Data.Chunks;

namespace AutoVoxel.Generator.Paralleliser;

/// <summary>
/// Asynchronously processes queues of tasks, allowing configurable maximum burst size and throttling.
/// </summary>
public class ParallelQueueWorker : IDisposable
{
    private readonly int MaxSingleShotSize;
    private readonly ManualResetEventSlim processQueueSlim = new(false);

    private readonly ConcurrentQueue<Task> TaskQueue = new();
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private Task generationTask;

    /// <summary>
    /// Asynchronously processes queues of tasks, allowing configurable maximum burst size and throttling.
    /// </summary>
    /// <param name="maxSingleShotSize">Maximum tasks to process in a single shot.</param>
    /// <param name="multishotCooldown">Throttle/cooldown applied between shots/bursts.</param>
    public ParallelQueueWorker(in int maxSingleShotSize = 32, in int multishotCooldown = 100)
    {
        MaxSingleShotSize = maxSingleShotSize;
    }

    public void StartTask()
    {
        // Start the generation loop
        generationTask = Task.Run(RunWorkerAsync, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        // Cancel the generation loop
        cancellationTokenSource.Cancel();

        // Wait for the task to end (blocking)
        generationTask.Wait();
    }

    public void Enqueue(in Task task)
    {
        TaskQueue.Enqueue(task);
        processQueueSlim.Set();
    }

    private async Task RunWorkerAsync()
    {
        while (!cancellationTokenSource.Token.IsCancellationRequested)
        {
            processQueueSlim.Wait(cancellationTokenSource.Token);
            processQueueSlim.Reset();

            // delay to allow tasks to pool in the queue
            await Task.Delay(100);

            await ProcessAll();
        }
    }

    private readonly List<Task> tasks = [];
    private async Task ProcessAll()
    {
        var sw = Stopwatch.StartNew();

        int queueCounter = 0;
        while (!TaskQueue.IsEmpty && TaskQueue.TryDequeue(out var task))
        {
            if (++queueCounter > MaxSingleShotSize)
                break; // throttle max tasks at a time
            tasks.Add(task);
        }


        await Task.WhenAll(tasks);
        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Success, $"ParallelQueueWorker work on {tasks.Count} tasks completed! Took {sw.Elapsed.TotalMilliseconds}ms");
        tasks.Clear();

        if (!TaskQueue.IsEmpty)
        {
            // allow cooldown
            await Task.Delay(100);
            processQueueSlim.Set();
        }

    }

    public void Dispose()
    {
        cancellationTokenSource.Dispose();
    }
}

