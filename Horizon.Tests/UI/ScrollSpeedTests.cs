using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class ScrollSpeedTests
{
    private sealed class CapturedTui : ITerminalProgram
    {
        public List<string> Inputs { get; } = [];
        public bool IsRunning => true;
        public string Name => "wheel test";
        public void Attach(TerminalView view) => view.Feed("\u001b[?1049h");
        public void Input(string bytes) => Inputs.Add(bytes);
        public void Resize(int columns, int rows) { }
        public void Update(float dt) { }
        public void Dispose() { }
    }
    private sealed class WheelProbe(bool content, bool take = true) : UIComponent
    {
        public float Heard;
        protected internal override bool UsesScrollSpeed => content;
        protected internal override bool OnScroll(float delta) { Heard += delta; return take; }
    }

    [Fact]
    public void Scrolling_scales_once_when_bubbling_but_other_gestures_keep_raw_units()
    {
        var parent = new WheelProbe(true);
        var child = parent.Add(new WheelProbe(false, false));
        Assert.Same(parent, UICompositor.GiveWheel(child, null, -1, 2.5f));
        Assert.Equal(-1, child.Heard);
        Assert.Equal(-2.5f, parent.Heard);
        var value = new WheelProbe(false);
        Assert.Same(value, UICompositor.GiveWheel(value, null, 1, 5));
        Assert.Equal(1, value.Heard);
    }

    [Fact]
    public void Fractional_terminal_scroll_accumulates_and_tui_events_stay_valid()
    {
        using var terminal = new TerminalView();
        terminal.Screen.Resize(40, 5);
        for (int i = 0; i < 30; i++) terminal.Screen.Feed("row\r\n");
        UICompositor.GiveWheel(terminal, null, 1, 0.25f);
        UICompositor.GiveWheel(terminal, null, 1, 0.25f);
        Assert.True(terminal.ScrolledUp > 0);
        var program = new CapturedTui();
        terminal.Start(program);
        for (int i = 0; i < 4; i++) UICompositor.GiveWheel(terminal, null, 1, 0.25f);
        Assert.Equal(3, program.Inputs.Count);
        program.Inputs.Clear();
        UICompositor.GiveWheel(terminal, null, 1, 2);
        Assert.Equal(6, program.Inputs.Count);
        Assert.All(program.Inputs, bytes => Assert.Equal("\u001b[A", bytes));
    }
}
