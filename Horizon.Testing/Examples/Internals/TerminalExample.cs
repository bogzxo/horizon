using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Internals;

/// <summary>
/// A terminal, the machine's shell running in a <see cref="TerminalView"/>, the first piece of Dusk
/// (docs/terminal-plan.md). Type into it, run vim or htop or a build in it, resize the window and watch the lines
/// reflow. The screen under it is <see cref="TerminalScreen"/>, fed by a <see cref="Core.Processes.PseudoTerminal"/>
/// (ConPTY on Windows). F2 starts the shell again, F3 switches the cursor's glide off and on.
/// <para>
/// In your own game or tool.
/// <code>
/// var terminal = module.AddComponent(new TerminalView { Fill = UIFill.Both });
/// terminal.Start();                       // the machine's shell, or Start("pwsh -NoLogo", folder)
/// terminal.OnExited = code => Close();
/// </code>
/// </para>
/// </summary>
public class TerminalExample : Scene, ITestControls
{
    public override Camera ActiveCamera { get; protected set; }

    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Keys", "type into the shell, shift+page up and down and the wheel read back"),
        new("Mouse", "drag selects, ctrl+c copies a selection, ctrl+v pastes"),
        new("F2", "start the shell again"),
        new("F3", "the cursor's glide off and on")
    ];

    private readonly UICompositor ui;
    private readonly TerminalView terminal;
    private readonly Label status;

    public TerminalExample()
    {
        var camera = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = camera;
        ui = AddComponent(new UICompositor(camera));

        var module = ui.CreateModule();
        terminal = module.AddComponent(new TerminalView
        {
            Anchor = Origin.Center,
            Font = UIFont.Shared("Assets/fonts/hack/", "hack.ttf", 96),
            TextScale = 0.16f
        });
        status = module.AddComponent(new Label { Anchor = Origin.Bottom, Position = new Vector2(0, 40), TextScale = 0.22f });

        terminal.OnExited = code => status.Text = $"the shell ended with {code}, F2 starts it again";
        terminal.OnTitle = title => status.Text = title;
        StartShell();
    }

    private void StartShell()
    {
        try
        {
            var started = terminal.Start();
            status.Text = $"{started.Command}  ({terminal.Columns} x {terminal.Rows})";
            terminal.Focus();
        }
        catch (Exception e)
        {
            status.Text = e.Message;
        }
    }

    public override void UpdateState(float dt)
    {
        // The terminal is the window less a margin, and follows the window as it is resized
        Vector2 size = Engine.WindowManager.ViewportSize;
        terminal.Size = new Vector2(MathF.Max(200.0f, size.X - 48.0f), MathF.Max(120.0f, size.Y - 120.0f));

        var keyboard = Engine.Input.Keyboard;
        if (keyboard.WasPressed(Key.F2))
            StartShell();
        if (keyboard.WasPressed(Key.F3))
            terminal.Animated = !terminal.Animated;

        base.UpdateState(dt);
    }

    protected override void DisposeOther()
    {
        terminal.Dispose();
        base.DisposeOther();
    }
}
