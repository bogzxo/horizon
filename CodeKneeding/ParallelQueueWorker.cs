using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace CodeKneading;

/// <summary>
/// Asynchronously processes queues of tasks, allowing configurable maximum burst size and throttling.
/// </summary>
public class ParallelQueueWorker : IDisposable
{
    private readonly int MaxSingleShotSize;
    private readonly int MultishotCooldown;
    private readonly ManualResetEventSlim processQueueSlim = new();
    private readonly ManualResetEventSlim hasDoneWorkSlim = new();
    private readonly List<Task> tasks = [];

    private readonly ConcurrentQueue<Task> TaskQueue = new();
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private Task generationTask;
    //private Thread generationThread;

    public delegate void TaskPoolCompletionDelegate();
    public event TaskPoolCompletionDelegate? TaskPoolCompletionCallback;

    public bool IsBusy { get => !TaskQueue.IsEmpty || tasks.Count > 0; }

    /// <summary>
    /// Asynchronously processes queues of tasks, allowing configurable maximum burst size and throttling.
    /// </summary>
    /// <param name="maxSingleShotSize">Maximum tasks to process in a single shot.</param>
    /// <param name="multishotCooldown">Throttle/cooldown applied between shots/bursts. Allows tasks to accumulate.</param>
    public ParallelQueueWorker(in int maxSingleShotSize = 128, in int multishotCooldown = 20)
    {
        MaxSingleShotSize = maxSingleShotSize;
        MultishotCooldown = multishotCooldown;
    }

    public void StartTask()
    {
        // Start the generation loop
        generationTask = Task.Run(RunWorkerAsync, cancellationTokenSource.Token);

        //generationThread = new Thread(new ThreadStart(RunThreadWorker)) { 
        //    IsBackground = true,
        //};
        //generationThread.Start();
    }

    public void Stop()
    {
        // Cancel the generation loop
        processQueueSlim.Set();
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

            await ProcessAllAsync();
        }
    }

    public void RunThreadWorker()
    {
        while (!cancellationTokenSource.Token.IsCancellationRequested)
        {
            processQueueSlim.Wait(cancellationTokenSource.Token);
            processQueueSlim.Reset();

            ProcessAll();
        }
    }

    private void ProcessAll()
    {
        var sw = Stopwatch.StartNew();

        int queueCounter = 0;

        while (TaskQueue.TryDequeue(out Task? task) && ++queueCounter < MaxSingleShotSize)
        {
            task.Start();
            tasks.Add(task);
        }

        Task.WaitAll(tasks);
        hasDoneWorkSlim.Set();
        tasks.Clear();

        TaskPoolCompletionCallback?.Invoke();

        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Success, $"ParallelQueueWorker work on {queueCounter} tasks completed! Took {sw.Elapsed.TotalMilliseconds}ms");

        if (!TaskQueue.IsEmpty)
        {
            // allow cooldown
            processQueueSlim.Set();

            // delay to allow tasks to pool in the queue
            Thread.Sleep(MultishotCooldown);
        }

    }

    private async Task ProcessAllAsync()
    {
        var sw = Stopwatch.StartNew();

        int queueCounter = 0;

        while (TaskQueue.TryDequeue(out Task? task) && ++queueCounter < MaxSingleShotSize)
        {
            task.Start();
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        hasDoneWorkSlim.Set();
        tasks.Clear();

        TaskPoolCompletionCallback?.Invoke();

        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Success, $"ParallelQueueWorker work on {queueCounter} tasks completed! Took {sw.Elapsed.TotalMilliseconds}ms");

        if (!TaskQueue.IsEmpty)
        {
            // allow cooldown
            processQueueSlim.Set();

            // delay to allow tasks to pool in the queue
            await Task.Delay(MultishotCooldown);
        }

    }

    public void WaitForSomeWorkCompletion()
    {
        hasDoneWorkSlim.Wait();
        hasDoneWorkSlim.Reset();
    }

    public bool AnyTasksDone()
    {
        if (hasDoneWorkSlim.IsSet)
        {
            hasDoneWorkSlim.Reset();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        cancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }
}