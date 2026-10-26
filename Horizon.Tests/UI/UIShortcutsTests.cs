using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Tests.UI;

/// <summary>
/// Two things on one key, a press going to the first that takes it and only that one, a command that can't be picked
/// passing it on, and a shortcut played from an input script as a chord.
/// </summary>
public class UIShortcutsTests
{
    // Ctrl+Shift+G pressed this update, nothing else
    private static void PressCtrlShiftG(UIShortcuts shortcuts, bool typing = false) =>
        shortcuts.Update(key => key == Key.G, control: true, shift: true, alt: false, typing);

    [Fact]
    public void One_press_fires_one_shortcut()
    {
        var shortcuts = new UIShortcuts();
        int first = 0, second = 0;
        shortcuts.Add(Key.G, () => { first++; return true; }, control: true, shift: true);
        shortcuts.Add(Key.G, () => { second++; return true; }, control: true, shift: true);

        PressCtrlShiftG(shortcuts);

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        // And the next press is a press again
        PressCtrlShiftG(shortcuts);
        Assert.Equal(2, first);
    }

    [Fact]
    public void A_shortcut_that_doesnt_take_the_press_falls_through_to_the_next()
    {
        var shortcuts = new UIShortcuts();
        int second = 0;
        shortcuts.Add(Key.G, () => false, control: true, shift: true);
        shortcuts.Add(Key.G, () => { second++; return true; }, control: true, shift: true);

        PressCtrlShiftG(shortcuts);

        Assert.Equal(1, second);
    }

    [Fact]
    public void A_first_shortcut_goes_before_the_ones_added_before_it()
    {
        var shortcuts = new UIShortcuts();
        string fired = string.Empty;
        shortcuts.Add(Key.G, () => { fired += "always"; return true; }, control: true, shift: true);
        shortcuts.Add(Key.G, () => { fired += "sometimes"; return true; }, control: true, shift: true, first: true);

        PressCtrlShiftG(shortcuts);

        Assert.Equal("sometimes", fired);
    }

    [Fact]
    public void A_shortcut_that_just_does_something_goes_off_whatever_else_does()
    {
        var shortcuts = new UIShortcuts();
        int bound = 0, taken = 0;
        shortcuts.Add(Key.G, () => { taken++; return true; }, control: true, shift: true);
        shortcuts.Add(Key.G, () => bound++, control: true, shift: true);

        PressCtrlShiftG(shortcuts);

        Assert.Equal((1, 1), (taken, bound));
    }

    [Fact]
    public void A_disabled_command_passes_its_key_to_the_next_command_on_it()
    {
        var commands = new UICommands();
        commands.Declare("View", "sides");
        commands.Declare("Layout", "group");
        bool layout = true, group = false;
        commands.ShowMenuWhen("Layout", () => layout);

        int sourceControl = 0, ungrouped = 0;
        commands.Add("View/sides", "Source Control", () => sourceControl++, "Ctrl+Shift+G", use: UIKeyUse.BoundWhileTyping);
        commands.Add("Layout/group", "Ungroup", () => ungrouped++, "Ctrl+Shift+G", use: UIKeyUse.Bound).IsEnabled = () => group;

        // A layout open with no group selected, Source Control's
        PressCtrlShiftG(commands.Keys);
        Assert.Equal((1, 0), (sourceControl, ungrouped));

        // A group selected, the designer's, though Source Control was added first
        group = true;
        PressCtrlShiftG(commands.Keys);
        Assert.Equal((1, 1), (sourceControl, ungrouped));

        // Typing into something Ungroup leaves the key alone, Source Control still goes
        PressCtrlShiftG(commands.Keys, typing: true);
        Assert.Equal((2, 1), (sourceControl, ungrouped));

        // No layout, no Layout menu, Source Control's again
        layout = false;
        PressCtrlShiftG(commands.Keys);
        Assert.Equal((3, 1), (sourceControl, ungrouped));
    }

    // One key pressed this update, with control or without
    private static void Press(UIShortcuts shortcuts, Key key, bool control = false, bool typing = false) =>
        shortcuts.Update(pressed => pressed == key, control, shift: false, alt: false, typing);

    // Nothing pressed this update
    private static void Idle(UIShortcuts shortcuts) => shortcuts.Update(_ => false, control: false, shift: false, alt: false, typing: false);

    [Fact]
    public void A_chord_goes_off_on_its_second_key()
    {
        var shortcuts = new UIShortcuts();
        int zen = 0;
        string? waiting = null;
        shortcuts.OnChordPending = first => waiting = first;
        shortcuts.Add(Key.K, () => zen++, control: true, then: new UIKeyStroke(Key.Z, false, false, false));

        Press(shortcuts, Key.K, control: true);
        Assert.Equal((0, "Ctrl+K", "Ctrl+K"), (zen, waiting, shortcuts.Pending));

        // Nothing pressed in between still waits
        Idle(shortcuts);
        Assert.Equal("Ctrl+K", shortcuts.Pending);

        Press(shortcuts, Key.Z);
        Assert.Equal(1, zen);
        Assert.Null(shortcuts.Pending);
        Assert.True(shortcuts.Swallowed);
    }

    [Fact]
    public void The_first_key_of_a_chord_alone_is_swallowed()
    {
        var shortcuts = new UIShortcuts();
        int chord = 0, single = 0;
        string? missed = null;
        shortcuts.OnChordMissed = keys => missed = keys;
        shortcuts.Add(Key.K, () => { chord++; return true; }, control: true, then: new UIKeyStroke(Key.S, true, false, false));
        shortcuts.Add(Key.K, () => single++, control: true);
        shortcuts.Add(Key.Q, () => single++);

        // The one key on Ctrl+K hears nothing of it, the chord is waited for
        Press(shortcuts, Key.K, control: true);
        Assert.Equal((0, 0), (chord, single));
        Assert.True(shortcuts.Swallowed);

        // A second key that's no chord's says so and sets off nothing, not even Q's own shortcut
        Press(shortcuts, Key.Q);
        Assert.Equal((0, 0, "Ctrl+K Q"), (chord, single, missed));

        // Escape stops the waiting without a word
        missed = null;
        Press(shortcuts, Key.K, control: true);
        Press(shortcuts, Key.Escape);
        Assert.Equal((0, null, null), (chord, missed, shortcuts.Pending));

        // And Ctrl+K Ctrl+S is the chord
        Press(shortcuts, Key.K, control: true);
        Press(shortcuts, Key.S, control: true);
        Assert.Equal((1, 0), (chord, single));
    }

    [Fact]
    public void The_wait_for_a_chords_second_key_runs_out()
    {
        var shortcuts = new UIShortcuts();
        double now = 10.0;
        int zen = 0, z = 0;
        shortcuts.Clock = () => now;
        shortcuts.Add(Key.K, () => zen++, control: true, then: new UIKeyStroke(Key.Z, false, false, false));
        shortcuts.Add(Key.Z, () => z++, whileTyping: true);

        Press(shortcuts, Key.K, control: true);
        now += shortcuts.ChordWait + 0.5;
        Idle(shortcuts);
        Assert.Null(shortcuts.Pending);

        // Too late, Z is Z again
        Press(shortcuts, Key.Z);
        Assert.Equal((0, 1), (zen, z));
    }

    [Fact]
    public void A_one_key_shortcut_on_another_key_goes_off_as_ever()
    {
        var shortcuts = new UIShortcuts();
        int save = 0, zen = 0;
        shortcuts.Add(Key.S, () => save++, control: true);
        shortcuts.Add(Key.K, () => zen++, control: true, then: new UIKeyStroke(Key.Z, false, false, false));

        Press(shortcuts, Key.S, control: true);
        Assert.Equal((1, 0), (save, zen));
        Assert.Null(shortcuts.Pending);
        Assert.False(shortcuts.Swallowed);

        // A chord's second key is its own, Ctrl+K Ctrl+S isn't a save
        shortcuts.Add(Key.K, () => { }, control: true, then: new UIKeyStroke(Key.S, true, false, false));
        Press(shortcuts, Key.K, control: true);
        Press(shortcuts, Key.S, control: true);
        Assert.Equal(1, save);

        // Taken off, the chord's key and the other chord stay as they were
        Assert.True(shortcuts.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false)));
        Assert.False(shortcuts.Has(Key.K, control: true, shift: false, alt: false));
        Assert.NotNull(shortcuts.Remove(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false)));
        Assert.False(shortcuts.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false)));
        Assert.True(shortcuts.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.S, true, false, false)));
    }

    [Fact]
    public void A_chord_that_isnt_for_typing_waits_for_nothing_while_typing()
    {
        var shortcuts = new UIShortcuts();
        shortcuts.Add(Key.K, () => { }, control: true, then: new UIKeyStroke(Key.Z, false, false, false));

        Press(shortcuts, Key.K, control: true, typing: true);
        Assert.Null(shortcuts.Pending);
    }

    [Fact]
    public void A_command_on_a_chord_is_picked_by_it()
    {
        var commands = new UICommands();
        commands.Declare("View", "toggles");
        int zen = 0;
        commands.Add("View/toggles", "Zen Mode", () => zen++, "Ctrl+K Z", use: UIKeyUse.BoundWhileTyping);

        Press(commands.Keys, Key.K, control: true, typing: true);
        Press(commands.Keys, Key.Z, typing: true);
        Assert.Equal(1, zen);
        Assert.True(commands.Keys.Has(new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false)));
    }

    [Theory]
    [InlineData("2.0 chord Ctrl+Shift+G")]
    [InlineData("2.0 chord ctrl+shift+key:g")]
    public void A_chord_in_a_script_holds_its_modifiers_round_a_tap_of_its_key(string line)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, line + "\n3.0 hold key:LeftControl 0.5\n");
            InputScript script = InputScript.Load(path, out List<string> problems);
            Assert.Empty(problems);

            var held = new HashSet<Key>();
            script.KeysAt(2.01f, held);
            Assert.True(held.SetEquals([Key.ControlLeft, Key.ShiftLeft]));

            script.KeysAt(2.08f, held);
            Assert.True(held.SetEquals([Key.ControlLeft, Key.ShiftLeft, Key.G]));

            script.KeysAt(2.16f, held);
            Assert.DoesNotContain(Key.G, held);
            Assert.Contains(Key.ControlLeft, held);

            script.KeysAt(2.5f, held);
            Assert.Empty(held);

            // LeftControl, the way people write it, is Silk's ControlLeft
            script.KeysAt(3.1f, held);
            Assert.True(held.SetEquals([Key.ControlLeft]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
