using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

using Horizon.Logging;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Engine.Debugging;

/// <summary>
/// The engine's console drawn over the game: the log as it comes in, and a line to type HIDL into that the
/// <see cref="Debuggers.DeveloperConsole"/> runs (<c>eng.print("hello")</c>, or anything a scene declared on its
/// runtime). Add one to the engine and press F4:
/// <code>
/// engine.AddEntity(new ConsoleOverlay());
/// </code>
/// It is a UIX layout like the <see cref="PerformanceOverlay"/>, on a screen UI of its own in the plain flat skin,
/// so it reads the same in every game. The web dashboard talks to the same console from outside the game, this is
/// the one for when the game is in front of you.
/// </summary>
public sealed class ConsoleOverlay : GameObject
{
    private const string SKIN_DIRECTORY = "Assets/uix/flat/";
    private const string SKIN_FILE = "skin.hor";

    // How many lines are kept, and how many are shown at once
    private const int KEPT = 400;
    private const int SHOWN = 18;

    private static readonly Vector4 Backdrop = new(0.04f, 0.05f, 0.08f, 0.88f);
    private static readonly Vector4 TextColour = new(0.93f, 0.95f, 1.0f, 1.0f);

    private UICompositor ui = null!;
    private UIModule module = null!;
    private StackPanel panel = null!;
    private Label output = null!;
    private TextBox input = null!;

    // Lines come in from the logger (any thread) and the console (the simulation thread), and are taken in on the updates
    private readonly ConcurrentQueue<string> incoming = new();
    private readonly List<string> lines = new(KEPT);
    private readonly StringBuilder text = new();
    private bool dirty = true, wasVisible;

    /// <summary>Whether the console is on screen. From any thread.</summary>
    public bool Visible { get; set; }

    /// <summary>The key that shows and hides it, null for none.</summary>
    public Key? ToggleKey { get; set; } = Key.F4;

    /// <summary>Whether what is written to the log shows up in the console as well as what the console says itself.</summary>
    public bool ShowLog { get; set; } = true;

    /// <summary>How big it's drawn, a unit per pixel at 1.</summary>
    public float Scale { get; set; } = 1.0f;

    public ConsoleOverlay(bool visible = false)
    {
        Name = "Console Overlay";
        Visible = visible;
    }

    public override void Initialize()
    {
        base.Initialize();

        ui = AddComponent(UICompositor.ForScreen(SKIN_DIRECTORY, SKIN_FILE));
        module = ui.CreateModule();
        module.ShowInLayoutDebugger = false;

        panel = module.AddComponent(new StackPanel
        {
            Padding = new UIEdges(12),
            Spacing = 8,
            Color = Backdrop,
            Radius = 6,
            Anchor = Origin.Top,
        });

        output = panel.Add(new Label { Color = TextColour, Align = Origin.BottomLeft, Anchor = Origin.Left, Wrap = true });
        input = panel.Add(new TextBox
        {
            Placeholder = "HIDL, enter to run (eng.print, eng.clear, help)",
            MaxLength = 512,
            Anchor = Origin.Left,
            OnSubmitted = Submit
        });

        Log.Written += OnLog;
        Engine.Console.Output += OnConsole;
    }

    private void OnLog(LogLevel level, string message)
    {
        if (ShowLog) Take($"[{level}] {message}");
    }

    private void OnConsole(string message, bool response)
    {
        if (message.Length > 0) Take(response ? message : $"> {message}");
    }

    private void Take(string line)
    {
        // Nobody wants the whole backlog if the game is spewing: what the queue holds beyond the kept lines is dropped
        if (incoming.Count < KEPT) incoming.Enqueue(line);
    }

    private void Submit(string command)
    {
        if (command.Length > 0) Engine.Console.Enqueue(command);

        input.Clear();
        input.Focus();
    }

    public override void UpdateState(float dt)
    {
        if (ToggleKey is { } key && Engine.Input.Keyboard.WasPressed(key))
            Visible = !Visible;

        while (incoming.TryDequeue(out string? line))
        {
            if (lines.Count == KEPT) lines.RemoveAt(0);
            lines.Add(line);
            dirty = true;
        }

        module.Enabled = Visible;
        if (Visible)
        {
            ui.Scale = Scale;

            // Across the top of the screen, as wide as it is
            Vector2 viewport = Engine.WindowManager.ViewportSize / Scale;
            panel.Size = new Vector2(viewport.X - 32.0f, 0.0f);
            panel.Position = new Vector2(0.0f, -16.0f);
            output.Size = new Vector2(viewport.X - 56.0f, 0.0f);
            input.Size = new Vector2(viewport.X - 56.0f, 0.0f);

            if (!wasVisible) input.Focus();

            if (dirty)
            {
                dirty = false;
                Write();
            }
        }

        wasVisible = Visible;

        // The UI of the overlay goes along with it, off or on
        base.UpdateState(dt);
    }

    private void Write()
    {
        text.Clear();
        int first = Math.Max(0, lines.Count - SHOWN);
        for (int i = first; i < lines.Count; i++)
        {
            if (i > first) text.Append('\n');
            text.Append(lines[i]);
        }

        output.Text = text.Length > 0 ? text.ToString() : "(nothing yet)";
    }

    protected override void DisposeOther()
    {
        Log.Written -= OnLog;
        Engine.Console.Output -= OnConsole;
        base.DisposeOther();
    }
}
