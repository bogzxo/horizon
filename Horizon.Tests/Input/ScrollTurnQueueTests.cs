using Horizon.Platform.Desktop;

namespace Horizon.Tests.Input;

/// <summary>Pairing the wheel turns the event watch saw with the events Silk says (SDL's whole notches), without SDL.</summary>
public class ScrollTurnQueueTests
{
    [Fact]
    public void Turns_pair_in_order()
    {
        var q = new ScrollTurnQueue();
        q.Add(0.0f, 0.0f, new ScrollTurn(0.1f, 0.5f, true));
        q.Add(0.0f, 1.0f, new ScrollTurn(0.0f, 1.0f, false));

        Assert.True(q.TryTake(0.0f, 0.0f, out ScrollTurn first));
        Assert.Equal(new ScrollTurn(0.1f, 0.5f, true), first);
        Assert.True(q.TryTake(0.0f, 1.0f, out ScrollTurn second));
        Assert.False(second.Precise);
        Assert.False(q.TryTake(0.0f, 1.0f, out _));
    }

    [Fact]
    public void Fractions_that_add_up_to_nothing_whole_keep_what_they_were()
    {
        // SDL hands Silk 0 for each of these and 1 once they add up, the turns are still each one's own fraction
        var q = new ScrollTurnQueue();
        q.Add(0.0f, 0.0f, new ScrollTurn(0.0f, 0.4f, true));
        q.Add(0.0f, 0.0f, new ScrollTurn(0.0f, 0.4f, true));
        q.Add(0.0f, 1.0f, new ScrollTurn(0.0f, 0.4f, true));

        float total = 0.0f;
        foreach (float whole in (float[])[0.0f, 0.0f, 1.0f])
        {
            Assert.True(q.TryTake(0.0f, whole, out ScrollTurn turn));
            total += turn.Y;
        }

        Assert.Equal(1.2f, total, 4);
    }

    [Fact]
    public void A_turn_whose_event_never_arrived_is_dropped()
    {
        var q = new ScrollTurnQueue();
        q.Add(0.0f, 2.0f, new ScrollTurn(0.0f, 2.0f, true)); // dropped by SDL
        q.Add(0.0f, 1.0f, new ScrollTurn(0.0f, 1.0f, false));

        Assert.True(q.TryTake(0.0f, 1.0f, out ScrollTurn turn));
        Assert.False(turn.Precise);
        Assert.False(q.TryTake(0.0f, 2.0f, out _));
    }

    [Fact]
    public void No_match_is_false_and_keeps_the_queue()
    {
        var q = new ScrollTurnQueue();
        q.Add(0.0f, 0.0f, new ScrollTurn(0.0f, 0.2f, true));

        Assert.False(q.TryTake(9.0f, 9.0f, out ScrollTurn turn));
        Assert.Equal(default, turn);
        Assert.True(q.TryTake(0.0f, 0.0f, out turn));
        Assert.True(turn.Precise);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var q = new ScrollTurnQueue();
        q.Add(1.0f, 1.0f, new ScrollTurn(1.0f, 1.0f, true));
        q.Clear();
        Assert.False(q.TryTake(1.0f, 1.0f, out _));
    }
}
