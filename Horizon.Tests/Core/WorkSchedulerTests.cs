using Horizon.Core.Threading;

namespace Horizon.Tests.Core;

/// <summary>
/// The work scheduler: a key runs once at a time, the work says how far it got, failing and being stopped end it as
/// they should, no more run at once than it was told, and what's to be done after is done where Pump is called.
/// </summary>
public class WorkSchedulerTests
{
    private static bool WaitFor(Func<bool> until, int milliseconds = 5000)
    {
        var started = DateTime.UtcNow;
        while (!until())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > milliseconds)
                return false;
            Thread.Sleep(5);
        }

        return true;
    }

    [Fact]
    public async Task A_key_that_is_going_is_not_started_again_and_whoever_asked_hears_when_it_ends()
    {
        var scheduler = new WorkScheduler();
        var go = new TaskCompletionSource();
        int ran = 0, told = 0;

        WorkItem first = scheduler.Start("update:csharp", "Updating", async context =>
        {
            Interlocked.Increment(ref ran);
            await go.Task;
        }, _ => told++);
        WorkItem second = scheduler.Start("update:csharp", "Updating again", _ => Task.CompletedTask, _ => told++);

        Assert.Same(first, second);
        Assert.True(scheduler.IsActive("update:csharp"));
        Assert.Equal(1, scheduler.Active);

        go.SetResult();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkState.Succeeded, first.State);
        Assert.Equal(1, ran);

        // Nothing is told until the UI's thread pumps
        Assert.Equal(0, told);
        scheduler.Pump();
        Assert.Equal(2, told);
        Assert.False(scheduler.IsActive("update:csharp"));

        // Over, the key can go again
        WorkItem third = scheduler.Start("update:csharp", "Updating", _ => Task.CompletedTask);
        Assert.NotSame(first, third);
    }

    [Fact]
    public async Task Work_says_how_far_it_got_and_what_it_wrote()
    {
        var scheduler = new WorkScheduler();
        var halfway = new TaskCompletionSource();
        var go = new TaskCompletionSource();
        WorkItem item = scheduler.Start("download", "Downloading", async context =>
        {
            context.Say("connecting");
            context.Report(0.5f, "half of it");
            context.Write("got 50 of 100");
            halfway.SetResult();
            await go.Task;
        });

        await halfway.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkState.Running, item.State);
        Assert.Equal(0.5f, item.Progress);
        Assert.Equal("got 50 of 100", item.Message);
        Assert.Equal(["got 50 of 100"], item.Output);
        int seen = item.Version;

        go.SetResult();
        await item.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1.0f, item.Progress);
        Assert.True(item.Version > seen);
        Assert.NotNull(item.Started);
        Assert.NotNull(item.Ended);
    }

    [Fact]
    public async Task Work_that_throws_failed_with_why_and_a_result_is_default()
    {
        var scheduler = new WorkScheduler();
        string? got = "not told";
        WorkItem item = scheduler.Start<string>("broken", "Breaking", _ => throw new InvalidOperationException("no network"), (_, result) => got = result);

        await item.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Pump();
        Assert.Equal(WorkState.Failed, item.State);
        Assert.Equal("no network", item.Error);
        Assert.Null(got);
    }

    [Fact]
    public async Task Stopped_work_is_cancelled_and_so_is_work_stopped_while_it_waited_its_turn()
    {
        var scheduler = new WorkScheduler(mostAtOnce: 1);
        WorkItem going = scheduler.Start("long", "Long", async context => await Task.Delay(Timeout.Infinite, context.Token));
        bool ranWaiting = false;
        WorkItem queued = scheduler.Start("next", "Next", _ => { ranWaiting = true; return Task.CompletedTask; });

        Assert.True(WaitFor(() => going.State == WorkState.Running));
        Assert.Equal(WorkState.Waiting, queued.State);

        queued.Cancel();
        going.Cancel();
        await going.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await queued.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkState.Cancelled, going.State);
        Assert.Equal(WorkState.Cancelled, queued.State);
        Assert.False(ranWaiting);
        Assert.Equal(0, scheduler.Active);
    }

    [Fact]
    public async Task No_more_run_at_once_than_it_was_told_and_the_rest_take_their_turn()
    {
        var scheduler = new WorkScheduler(mostAtOnce: 2);
        int now = 0, most = 0;
        var items = Enumerable.Range(0, 6).Select(i => scheduler.Start($"work {i}", "Work", async _ =>
        {
            int at = Interlocked.Increment(ref now);
            InterlockedMax(ref most, at);
            await Task.Delay(30);
            Interlocked.Decrement(ref now);
        })).ToList();

        await Task.WhenAll(items.Select(item => item.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(items, item => Assert.Equal(WorkState.Succeeded, item.State));
        Assert.Equal(2, most);

        // The newest first, waiting and going before what ended
        scheduler.Pump();
        Assert.Equal("work 5", scheduler.Items[0].Key);
    }

    [Fact]
    public async Task Work_that_cant_be_stopped_isnt()
    {
        var scheduler = new WorkScheduler();
        var go = new TaskCompletionSource();
        WorkItem item = scheduler.Start("settings", "Saving", async _ => await go.Task, cancellable: false);
        item.Cancel();
        go.SetResult();
        await item.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkState.Succeeded, item.State);
    }

    [Fact]
    public async Task Added_and_ended_are_said_on_the_thread_that_pumps()
    {
        var scheduler = new WorkScheduler();
        var heard = new List<string>();
        scheduler.Added += item => heard.Add("added " + item.Key);
        scheduler.Ended += item => heard.Add("ended " + item.Key);
        int woken = 0;
        scheduler.Woken = () => Interlocked.Increment(ref woken);

        WorkItem item = scheduler.Start("look", "Looking", _ => Task.CompletedTask);
        await item.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(heard);
        Assert.True(WaitFor(() => Volatile.Read(ref woken) > 0));

        scheduler.Pump();
        Assert.Equal(["added look", "ended look"], heard);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
