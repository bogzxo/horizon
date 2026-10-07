using Horizon.Core.Threading;

namespace Horizon.Tests;

public class TurnGateTests
{
    [Fact]
    public void Whoever_asked_first_is_let_in_first()
    {
        var gate = new TurnGate();
        var order = new List<int>();
        var threads = new List<Thread>();

        gate.Enter();

        // Each one queues up after the one before it has
        for (int i = 0; i < 5; i++)
        {
            int id = i;
            var queued = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                queued.Set();
                gate.Enter();
                lock (order) order.Add(id);
                gate.Exit();
            });

            thread.Start();
            queued.Wait();

            // Long enough for it to have taken its ticket
            SpinUntil(() => gate.HasWaiters && WaitersAtLeast(gate, id + 1));
            threads.Add(thread);
        }

        gate.Exit();
        foreach (var thread in threads) thread.Join();

        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public void Leaving_and_asking_again_goes_to_the_back_of_the_line()
    {
        var gate = new TurnGate();
        var turns = new List<string>();

        gate.Enter();

        var waiter = new Thread(() =>
        {
            gate.Enter();
            lock (turns) turns.Add("waiter");
            gate.Exit();
        });
        waiter.Start();
        SpinUntil(() => gate.HasWaiters);

        // Out and straight back in: the waiter was there first
        gate.Exit();
        gate.Enter();
        lock (turns) turns.Add("again");
        gate.Exit();

        waiter.Join();
        Assert.Equal(["waiter", "again"], turns);
    }

    [Fact]
    public void The_same_thread_can_go_in_again()
    {
        var gate = new TurnGate();

        gate.Enter();
        gate.Enter();
        Assert.True(gate.IsHeldByCurrentThread);

        gate.Exit();
        Assert.True(gate.IsHeldByCurrentThread);

        gate.Exit();
        Assert.False(gate.IsHeldByCurrentThread);
    }

    [Fact]
    public void Coming_out_without_going_in_is_refused()
    {
        var gate = new TurnGate();
        Assert.Throws<SynchronizationLockException>(gate.Exit);
    }

    [Fact]
    public void A_pause_lets_somebody_else_in_until_they_signal()
    {
        var gate = new TurnGate();
        bool helped = false;

        gate.Enter();

        var helper = new Thread(() =>
        {
            gate.Enter();
            helped = true;
            gate.Signal();
            gate.Exit();
        });
        helper.Start();
        SpinUntil(() => gate.HasWaiters);

        bool signalled = gate.Pause(TimeSpan.FromSeconds(5));

        Assert.True(signalled);
        Assert.True(helped);
        Assert.True(gate.IsHeldByCurrentThread);

        gate.Exit();
        helper.Join();
    }

    [Fact]
    public void A_pause_nobody_answers_ends_on_its_own()
    {
        var gate = new TurnGate();

        gate.Enter();
        bool signalled = gate.Pause(TimeSpan.FromMilliseconds(20));

        Assert.False(signalled);
        Assert.True(gate.IsHeldByCurrentThread);
        gate.Exit();
    }

    [Fact]
    public void Many_threads_never_overlap()
    {
        var gate = new TurnGate();
        int inside = 0, overlaps = 0;
        long turns = 0;

        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                gate.Enter();
                if (Interlocked.Increment(ref inside) != 1) Interlocked.Increment(ref overlaps);
                turns++;
                Interlocked.Decrement(ref inside);
                gate.Exit();
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Assert.Equal(0, overlaps);
        Assert.Equal(8000, turns);
    }

    private static bool WaitersAtLeast(TurnGate gate, int count)
    {
        // Tickets handed out minus the one being served is how many are in line, plus the one inside
        var next = (long)typeof(TurnGate).GetField("nextTicket", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(gate)!;
        var serving = (long)typeof(TurnGate).GetField("serving", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(gate)!;
        return next - serving - 1 >= count;
    }

    private static void SpinUntil(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "timed out waiting");
    }
}
