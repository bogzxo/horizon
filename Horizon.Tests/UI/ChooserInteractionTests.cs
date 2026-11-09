using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public sealed class ChooserInteractionTests : IDisposable
{
    private readonly UICompositor compositor = new(new Camera2D(new Vector2(800, 600)));
    private readonly UIModule module;
    private readonly bool motion = UIDrawList.Motion;
    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));

    public ChooserInteractionTests()
    {
        UIDrawList.Motion = false;
        module = compositor.CreateModule();
    }

    private void Layout()
    {
        module.Root.MeasureTree(Skin);
        module.Root.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(800, 600)));
    }

    private static IEnumerable<UIComponent> All(UIComponent component) =>
        component.Children.SelectMany(child => new[] { child }.Concat(All(child)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outside_press_cancels_palette_once_and_restores_focus(bool secondary)
    {
        var before = module.AddComponent(new TextBox());
        before.Focus();
        int cancelled = 0, accepted = 0, closed = 0;
        var palette = UIDialog.ShowPalette(module, "Pick", new DialogChoice("Go", () => accepted++), new DialogChoice("Cancel", () => cancelled++));
        palette.Closed = () => closed++;
        Layout();
        var outside = palette.Bounds.Min + new Vector2(5);
        if (secondary) palette.OnContextMenu!(outside);
        else palette.OnPointerDown(outside);
        Assert.False(palette.IsOpen);
        Assert.Equal((0, 1, 1), (accepted, cancelled, closed));
        palette.Cancel();
        Assert.False(palette.IsOpen);
        Assert.Equal((0, 1, 1), (accepted, cancelled, closed));
        Assert.Same(before, compositor.Focus);
    }

    [Fact]
    public void Outside_press_does_not_answer_confirmation()
    {
        int picked = 0;
        var dialog = UIDialog.Show(module, "Confirm", "", new DialogChoice("Delete", () => picked++, Risky: true), new DialogChoice("Cancel"));
        Layout();
        dialog.OnPointerDown(dialog.Bounds.Min + new Vector2(5));
        dialog.OnContextMenu!(dialog.Bounds.Min + new Vector2(5));
        Assert.True(dialog.IsOpen);
        Assert.Equal(0, picked);
        Assert.Same(dialog.Buttons.Last(), compositor.Focus);
    }

    [Fact]
    public void Transient_preview_can_opt_into_outside_cancellation_without_changing_confirmation_policy()
    {
        int opened = 0;
        var dialog = UIDialog.Show(module, "Peek", "", new DialogChoice("Open", () => opened++), new DialogChoice("Close"));
        dialog.Dims = false;
        dialog.DismissOnOutsidePress = true;
        Layout();
        dialog.OnPointerDown(dialog.Bounds.Min + new Vector2(5));
        Assert.False(dialog.IsOpen);
        Assert.Equal(0, opened);
    }

    [Fact]
    public void No_match_submission_keeps_query_and_cannot_execute_stale_selection()
    {
        int picked = 0;
        var dialog = UIChooser.Show(module, "Pick", "", new List<(string Text, Action Picked)> { ("Open", () => picked++) });
        var query = All(dialog).OfType<TextBox>().Single();
        query.Text = "no-match";
        query.OnChanged!(query.Text);
        query.OnSubmitted!(query.Text);
        Assert.True(dialog.IsOpen);
        Assert.Same(query, compositor.Focus);
        Assert.Equal("no-match", query.Text);
        Assert.Equal(0, picked);
        query.Text = "Open";
        query.OnChanged!(query.Text);
        query.OnSubmitted!(query.Text);
        query.OnSubmitted!(query.Text);
        Assert.False(dialog.IsOpen);
        Assert.Equal(1, picked);
    }

    [Fact]
    public void A_searched_list_is_asked_off_the_thread_and_enter_waits_for_it()
    {
        // Go to All's way, the rows worked out by whoever asked for each thing typed, best first, and Enter pressed before
        // they come back picks the best of them once they do
        int picked = 0;
        var asked = new List<string>();
        var gate = new ManualResetEventSlim(false);
        UIChoiceSearch search = (typed, cancel) =>
        {
            lock (asked) asked.Add(typed);
            gate.Wait(TimeSpan.FromSeconds(5));
            return typed.Length == 0 ? [] : [new UIChoice("SimulationLoop", () => picked++), new UIChoice("SimulationLoopTests", () => picked += 10)];
        };

        var dialog = UIChooser.Show(module, "Go to All", "", new List<UIChoice>(), searched: prefix => prefix == '\0' ? search : null);
        var query = All(dialog).OfType<TextBox>().Single();
        query.Text = "simloop";
        query.OnChanged!(query.Text);
        query.OnSubmitted!(query.Text);
        Assert.True(dialog.IsOpen);

        gate.Set();
        for (int tick = 0; tick < 200 && dialog.IsOpen; tick++)
        {
            Thread.Sleep(5);
            dialog.UpdateTree(0.01f);
        }

        Assert.False(dialog.IsOpen);
        Assert.Equal(1, picked);
        lock (asked) Assert.Contains("simloop", asked);
    }

    [Fact]
    public void Decorative_dialog_press_preserves_its_input_but_workspace_press_does_not()
    {
        var workspace = module.AddComponent(new Panel());
        var dialog = UIChooser.Show(module, "Pick", "", new List<(string Text, Action Picked)>());
        var query = All(dialog).OfType<TextBox>().Single();
        var decoration = dialog.Body.Add(new Label("Nothing matches"));
        Assert.Same(query, compositor.FocusForPress(decoration));
        Assert.Null(compositor.FocusForPress(workspace));
    }

    [Fact]
    public void Replacing_dialogs_rapidly_never_leaves_two_open_behind_an_exit_animation()
    {
        UIDrawList.Motion = true;
        var first = UIDialog.ShowPalette(module, "First", new DialogChoice("Cancel"));
        var second = UIDialog.ShowPalette(module, "Second", new DialogChoice("Cancel"));
        var third = UIDialog.ShowPalette(module, "Third", new DialogChoice("Cancel"));
        Assert.False(first.IsOpen);
        Assert.False(second.IsOpen);
        Assert.True(third.IsOpen);
        Assert.Single(module.Root.Children.OfType<UIDialog>(), dialog => dialog.IsOpen);
    }

    [Fact]
    public void Closing_palette_children_cannot_capture_pointer_during_exit()
    {
        var dialog = UIChooser.Show(module, "Pick", "", new List<(string Text, Action Picked)> { ("Open", () => { }) });
        Layout();
        var query = All(dialog).OfType<TextBox>().Single();
        UIDrawList.Motion = true;
        dialog.Close();
        Assert.Null(dialog.HitTest(query.Bounds.Center));
        Assert.False(dialog.IsOpen);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void Cancelled_dialog_is_skipped_by_tab_and_navigation_during_exit(int direction, bool palette)
    {
        UIDrawList.Motion = true;
        var first = module.AddComponent(new Button("First") { OnPressed = () => { } });
        var second = module.AddComponent(new Button("Second") { OnPressed = () => { } });
        var before = direction > 0 ? second : first;
        var expected = direction > 0 ? first : second;
        before.Focus();
        module.Navigation.Select(before);
        var dialog = palette
            ? UIChooser.Show(module, "Pick", "", new List<(string Text, Action Picked)> { ("Open", () => { }) })
            : UIDialog.Show(module, "Confirm", "", new DialogChoice("Cancel"));
        dialog.Cancel();
        Assert.False(dialog.IsOpen);
        Assert.NotNull(dialog.Parent); // Still drawn: this is the exit-animation interval.
        Assert.Same(before, compositor.Focus);

        compositor.FocusNext(direction); // The route used by Tab / Shift+Tab.
        Assert.Same(expected, compositor.Focus);
        if (direction > 0) module.Navigation.Next();
        else module.Navigation.Previous();
        Assert.Same(expected, module.Navigation.Current);

        module.Root.Remove(dialog);
        Assert.Same(expected, compositor.Focus);
    }

    public void Dispose()
    {
        compositor.RemoveModule(module);
        UIDrawList.Motion = motion;
    }
}
