using System.Numerics;

using Horizon.Core.Threading;

namespace Horizon.UI.Components;

/// <summary>
/// Work going on off the UI's thread (a <see cref="WorkScheduler"/>) where somebody can see it. A piece of work that's
/// asked to be seen gets a card in the corner while it goes (<see cref="Notifications"/>), a bar of how far it got and
/// what it's doing right now, Cancel if it can be stopped, and how it ended once it has, Output when it wrote anything.
/// A piece of the status bar says how many are going, and a click on it is the menu of them, stopping one or reading
/// what it wrote. Hex's language server installs and every look at GitHub go through one of these.
/// <para>
/// <see cref="Update"/> every tick does the lot, and does nothing (makes nothing) while nothing is going on.
/// </para>
/// </summary>
public sealed class UIWorkCards
{
    private readonly Notifications toasts;

    // The cards of the work that's seen, and the version of it each last showed
    private readonly Dictionary<WorkItem, (UINotification Card, int Shown)> cards = [];

    private StatusItem? item;

    /// <summary>The work, what to start it on and to ask about it.</summary>
    public WorkScheduler Work { get; }

    /// <summary>The colour of a card of work that went wrong, null for the skin's colour of an error.</summary>
    public Vector4? ErrorColor { get; set; }

    /// <summary>What Output on a card (or a piece of work picked in the menu) does with what it wrote, null for no button.</summary>
    public Action<WorkItem>? ShowOutput { get; set; }

    /// <summary>Called with what a card of work that went wrong says, for a program that keeps a log of its own.</summary>
    public Action<string>? Failed { get; set; }

    /// <summary>Which work the status bar and the menu are about, all of it unless it says (Hex leaves out its own quick ones).</summary>
    public Func<WorkItem, bool>? Listed { get; set; }

    /// <summary>The cards of the work going that has one, for a test to look at.</summary>
    public IReadOnlyDictionary<WorkItem, (UINotification Card, int Shown)> Cards => cards;

    /// <summary>The piece of the status bar, once <see cref="AddTo"/> made it.</summary>
    public StatusItem? Item => item;

    public UIWorkCards(WorkScheduler work, Notifications toasts)
    {
        Work = work;
        this.toasts = toasts;
        work.Ended += Ended;
    }

    /// <summary>The count of what's going on a status bar, a click on it the menu of the work, there only while something goes.</summary>
    public StatusItem AddTo(StatusBar bar, UIModule menus, string tooltip = "")
    {
        item = bar.Add(string.Empty, right: true, pressed: () => ContextMenu.ShowAt(menus, menus.Compositor.Pointer.Position, Menu()), tooltip: tooltip);
        item.IsVisible = () => Work.Active > 0;
        return item;
    }

    /// <summary>
    /// Starts a piece of work known by a key, or hands back the one of that key already going. <paramref name="seen"/>
    /// puts a card up for it while it goes (brings it back up when it's going already). <paramref name="done"/> on the UI's
    /// thread once it's over.
    /// </summary>
    public WorkItem Start(string key, string title, Func<WorkContext, Task> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true)
    {
        bool fresh = !Work.IsActive(key);
        WorkItem started = Work.Start(key, title, what, done, cancellable);
        if (seen && fresh)
            Show(started);
        else if (seen)
            Renew(started);
        return started;
    }

    /// <summary>The work of a blocking call, on a thread of the pool from start to end.</summary>
    public WorkItem Start(string key, string title, Action<WorkContext> what, Action<WorkItem>? done = null, bool seen = true, bool cancellable = true) =>
        Start(key, title, context => Task.Run(() => what(context), context.Token), done, seen, cancellable);

    /// <summary>A piece of work that's going brought to notice again, its card back up (or put up, if it had none or was closed).</summary>
    public void Renew(WorkItem going)
    {
        if (!going.IsActive)
            return;

        if (cards.TryGetValue(going, out var shown) && !shown.Card.Closed)
            shown.Card.Renew();
        else
            Show(going);
    }

    // a card for a piece of work, kept up to date by Update
    private void Show(WorkItem going)
    {
        UINotification card = toasts.Show(going.Title, "sync", null, 0.0f);
        card.Progress = float.NaN;
        if (going.Cancellable)
            card.Actions.Add(("Cancel", going.Cancel));
        cards[going] = (card, -1);
    }

    // What the status item said last, so it's only written again when that changes
    private (WorkItem?, string?, int, int) itemSays;

    // The work with cards this tick, gathered once so the cards can change as they're gone through
    private readonly List<WorkItem> cardsNow = [];

    /// <summary>Every tick, what the work that ended left for the UI's thread, and the cards and the count brought up to date.</summary>
    public void Update()
    {
        Work.Pump();

        int going = Work.Active;
        if (going > 0 && item is not null)
        {
            // Written again only when what it says changes, the work, how many, or its progress by the percent
            WorkItem? first = Work.NewestActive(Listed);
            int percent = first?.Progress is { } progress and < 1.0f ? (int)(progress * 100.0f) : -1;
            if (itemSays != (first, first?.Title, going, percent))
            {
                itemSays = (first, first?.Title, going, percent);
                string text = first is null ? $"$(sync) {going} going" : going == 1 ? $"$(sync) {first.Title}" : $"$(sync) {first.Title} and {going - 1} more";
                item.Text = percent >= 0 ? $"{text} {percent}%" : text;
            }
        }

        // every tick, so nothing is made when there's nothing to show
        if (cards.Count == 0)
            return;

        // The cards are changed as they're gone through, so the work is gathered first, into the same list every tick
        cardsNow.Clear();
        cardsNow.AddRange(cards.Keys);
        foreach (WorkItem work in cardsNow)
        {
            var (card, shown) = cards[work];
            // The version read before what it covers, a change made on the work's thread in between is seen next tick
            int version = work.Version;
            if (version == shown)
                continue;

            card.Detail = work.Message;
            card.Progress = work.State == WorkState.Waiting ? float.NaN : work.Progress ?? float.NaN;
            cards[work] = (card, version);
        }
    }

    // a piece of work that ended, its card saying how, on the UI's thread
    private void Ended(WorkItem ended)
    {
        if (!cards.Remove(ended, out var shown))
            return;

        UINotification card = shown.Card;
        bool closed = card.Closed;
        IReadOnlyList<string> output = ended.Output;
        string took = ended.Started is { } started && ended.Ended is { } stopped ? $", {(stopped - started).TotalSeconds:0.#} s" : string.Empty;

        // Closed by its x while it went is out of sight, unless it failed, which wants saying
        if (closed && ended.State != WorkState.Failed)
            return;
        Vector4? error = ErrorColor ?? toasts.Module?.Compositor.Skin?.ErrorColor;
        if (closed)
            card = toasts.Show(string.Empty, "error", error, 0.0f);

        card.Progress = null;
        card.Actions.Clear();
        switch (ended.State)
        {
            case WorkState.Succeeded:
                card.Text = $"{ended.Title}, done{took}";
                card.Icon = "check";
                card.IconColor = null;
                card.Seconds = 6.0f;
                break;
            case WorkState.Cancelled:
                card.Text = $"{ended.Title}, stopped";
                card.Icon = "stop";
                card.Seconds = 4.0f;
                break;
            default:
                card.Text = $"{ended.Title} didn't work, {ended.Error}";
                card.Icon = "error";
                card.IconColor = error;
                card.Seconds = 0.0f;
                Failed?.Invoke(card.Text);
                break;
        }

        card.Detail = ended.State == WorkState.Succeeded ? string.Empty : ended.Message;
        if (output.Count > 0 && ShowOutput is { } show)
            card.Actions.Add(("Output", () => show(ended)));
        card.Renew();
    }

    /// <summary>The work going and what ended lately, a click on what's going stopping it, on what ended its output.</summary>
    public Menu Menu()
    {
        var shown = new Menu("work");
        foreach (WorkItem work in Work.Items.Where(work => Listed?.Invoke(work) ?? true).Take(12))
        {
            string state = work.State switch
            {
                WorkState.Waiting => "waiting",
                WorkState.Running => work.Progress is { } progress ? progress.ToString("P0") : "going",
                WorkState.Succeeded => "done",
                WorkState.Cancelled => "stopped",
                _ => "failed"
            };

            if (work.IsActive && work.Cancellable)
                shown.Add($"Stop {work.Title}", work.Cancel, state, "stop");
            else
                shown.Add(work.Title, () => ShowOutput?.Invoke(work), state, work.State == WorkState.Failed ? "error" : work.IsActive ? "sync" : "check");
        }

        if (shown.Items.Count == 0)
            shown.Add("Nothing going", () => { }, string.Empty).IsEnabled = () => false;
        return shown;
    }
}
