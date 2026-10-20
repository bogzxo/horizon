using Horizon.Core.Threading;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Horizon.Tests.UI;

/// <summary>Work as somebody sees it, a card while it goes and how it ended after, and the glass and the text it's on.</summary>
public class UIWorkCardsTests
{
    // Pumped until the work's ended or a few seconds went by, the way a program's ticks would
    private static void PumpUntil(UIWorkCards cards, Func<bool> done)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            cards.Update();
            Thread.Sleep(5);
        }

        cards.Update();
    }

    [Fact]
    public void A_seen_piece_of_work_has_a_card_while_it_goes_that_says_how_it_ended()
    {
        var cards = new UIWorkCards(new WorkScheduler(), new Notifications());
        using var hold = new ManualResetEventSlim();

        WorkItem going = cards.Start("build", "Building", context =>
        {
            context.Report(0.5f, "half way");
            hold.Wait(TimeSpan.FromSeconds(5));
        });
        PumpUntil(cards, () => going.Progress is 0.5f);

        UINotification card = Assert.Single(cards.Cards).Value.Card;
        Assert.Equal("Building", card.Text);
        Assert.Equal(0.5f, card.Progress);
        Assert.Contains(card.Actions, action => action.Label == "Cancel");

        hold.Set();
        PumpUntil(cards, () => !going.IsActive);

        Assert.Empty(cards.Cards);
        Assert.StartsWith("Building, done", card.Text);
        Assert.Null(card.Progress);
    }

    [Fact]
    public void Started_again_while_it_goes_it_is_the_one_piece_of_work_and_the_one_card()
    {
        var cards = new UIWorkCards(new WorkScheduler(), new Notifications());
        using var hold = new ManualResetEventSlim();

        WorkItem first = cards.Start("install", "Installing", _ => hold.Wait(TimeSpan.FromSeconds(5)));
        WorkItem second = cards.Start("install", "Installing", _ => { });
        cards.Update();

        Assert.Same(first, second);
        Assert.Single(cards.Cards);

        hold.Set();
        PumpUntil(cards, () => !first.IsActive);
    }

    [Fact]
    public void One_that_failed_says_why_and_tells_the_program()
    {
        string? told = null;
        var cards = new UIWorkCards(new WorkScheduler(), new Notifications()) { Failed = text => told = text };

        WorkItem going = cards.Start("look", "Looking", (Action<WorkContext>)(_ => throw new InvalidOperationException("no network")));
        PumpUntil(cards, () => !going.IsActive);

        Assert.NotNull(told);
        Assert.Contains("didn't work, no network", told);
    }

    [Fact]
    public void The_glass_presets_hold_the_sliders_to_what_still_looks_like_glass()
    {
        UIGlass lens = UIGlass.Lens(100.0f, 2.0f, -1.0f, 50.0f);
        Assert.Equal((64.0f, 1.0f, 0.0f, 24.0f), (lens.Blur, lens.Clarity, lens.Rim, lens.Bend));

        UIGlass pane = UIGlass.Pane(UIGlass.Clear.Blur, UIGlass.Clear.Clarity, UIGlass.Clear.Rim);
        Assert.Equal(0.0f, pane.Bend);
        Assert.True(pane.Blur > UIGlass.Clear.Blur && pane.Rim < UIGlass.Clear.Rim);
    }

    [Fact]
    public void What_somebody_wrote_goes_in_a_label_without_its_brackets_taken_for_markup()
    {
        Assert.Equal("a (b) c", UIRichText.Plain("a [b] c"));
        Assert.Equal("list[0]", UIRichText.Literal("list[0]"));
        Assert.Equal("(b)bold(/b)", UIRichText.Literal("[b]bold[/b]"));
    }
}
