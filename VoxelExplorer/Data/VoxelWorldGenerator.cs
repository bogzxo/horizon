using System.Collections.Concurrent;
using System.Diagnostics;

namespace VoxelExplorer.Data;

// Helper class to delegate chunk data generation asynchronously
internal class VoxelWorldGenerator : IDisposable
{
    private readonly ConcurrentQueue<Chunk> chunkQueue;
    private readonly CancellationTokenSource cancellationTokenSource;
    private Task generationTask;

    public VoxelWorldGenerator()
    {
        chunkQueue = new ConcurrentQueue<Chunk>();
        cancellationTokenSource = new CancellationTokenSource();
    }

    public void StartTask()
    {
        // Start the generation loop
        generationTask = Task.Run(GenerateChunksAsync, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        // Cancel the generation loop
        cancellationTokenSource.Cancel();

        // Wait for the task to end (blocking)
        generationTask.Wait();
    }

    public void EnqueueChunk(Chunk chunk)
    {
        // Enqueue chunk for generation
        chunkQueue.Enqueue(chunk);
    }

    private async Task GenerateChunksAsync()
    {
        while (!cancellationTokenSource.Token.IsCancellationRequested)
        {
            if (!chunkQueue.IsEmpty)
            {
                // TODO: exception handling
                await GenerateAllAsync();
            }

            // Delay for a short time before checking the queue again
            await Task.Delay(250);
        }

    }

    private readonly List<Task> tasks = [];
    private async Task GenerateAllAsync()
    {
        var sw = Stopwatch.StartNew();

        while (!chunkQueue.IsEmpty && chunkQueue.TryDequeue(out var chunk))
            tasks.Add(chunk.GenerateDataAsync());

        await Task.WhenAll(tasks);
        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Success, $"ChunkBatch data generation on {tasks.Count} chunks completed! Took {sw.Elapsed.Milliseconds}ms");

        tasks.Clear();
    }

    public void Dispose()
    {
        cancellationTokenSource.Dispose();
    }
}

// Helper class to delegate chunk data generation asynchronously
internal class VoxelMeshGenerator : IDisposable
{
    private readonly ConcurrentQueue<Chunk> chunkQueue;
    private readonly CancellationTokenSource cancellationTokenSource;
    private Task generationTask;

    public VoxelMeshGenerator()
    {
        chunkQueue = new ConcurrentQueue<Chunk>();
        cancellationTokenSource = new CancellationTokenSource();
    }

    public void StartTask()
    {
        // Start the generation loop
        generationTask = Task.Factory.StartNew(GenerateMeshesAsync, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        // Cancel the generation loop
        cancellationTokenSource.Cancel();

        // Wait for the task to end (blocking)
        generationTask.Wait();
    }

    public void EnqueueChunk(Chunk chunk)
    {
        // Enqueue chunk for generation
        chunkQueue.Enqueue(chunk);
    }

    private async Task GenerateMeshesAsync()
    {
        while (!cancellationTokenSource.Token.IsCancellationRequested)
        {
            if (!chunkQueue.IsEmpty)
            {
                // TODO: exception handling
                await GenerateAllAsync();
            }

            // Delay for a short time before checking the queue again
            await Task.Delay(250);
        }

    }

    private readonly List<Task> tasks = [];
    private async Task GenerateAllAsync()
    {
        var sw = Stopwatch.StartNew();

        while (!chunkQueue.IsEmpty && chunkQueue.TryDequeue(out var chunk))
            tasks.Add(chunk.GenerateMeshAsync());

        await Task.WhenAll(tasks);
        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Success, $"ChunkBatch mesh generation on {tasks.Count} chunks completed! Took {sw.Elapsed.Milliseconds}ms");

        tasks.Clear();
    }

    public void Dispose()
    {
        cancellationTokenSource.Dispose();
    }
}
