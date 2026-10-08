using System;
using System.Numerics;
using System.Text;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Input;

/// <summary>
/// The gamepads of Horizon.Input2. On the left is every gamepad there is with what is held on it and which
/// actions that makes, along with one that isn't a device at all and is driven from the keyboard; on the right
/// is how the checks went that are made as the scene starts: bindings, combinations, a gamepad that is fed by
/// hand, and all of it written out as HIDL and read back.
/// </summary>
public class GamepadExample : Scene, ITestControls
{
    private static readonly Vector2 DesignSize = new(1600, 900);

    // What the keyboard holds down on the gamepad that isn't one
    private static readonly (Key Key, GamepadInput Input)[] Keys =
    [
        (Key.W, GamepadInput.DPadUp), (Key.A, GamepadInput.DPadLeft), (Key.S, GamepadInput.DPadDown), (Key.D, GamepadInput.DPadRight),
        (Key.J, GamepadInput.A), (Key.K, GamepadInput.B), (Key.L, GamepadInput.RightTrigger)
    ];

    private const string ACTION_JUMP = "jump";
    private const string ACTION_FIRE = "fire";
    private const string ACTION_SPECIAL = "special";

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("W A S D", "the d-pad of the keyboard's gamepad"),
        new("J / K / L", "its A, its B and its right trigger"),
        new("J + K", "both at once, which is an action of its own")
    ];

    private readonly UICompositor _compositor;
    private readonly GamepadInputManager _gamepads;
    private Gamepad _keyboardPad = null!;
    private Label _live = null!;
    private readonly List<GamepadInput> _held = [];

    public GamepadExample()
    {
        var cam = AddEntity(new Camera2D(Engine.WindowManager.ViewportSize));
        ActiveCamera = cam;

        // What every gamepad starts out bound to, the ones that are plugged in later as well
        _gamepads = AddEntity(new GamepadInputManager());
        _gamepads.DefaultBindings
            .Bind(ACTION_JUMP, GamepadInput.A)
            .Bind(ACTION_FIRE, GamepadInput.RightTrigger)
            .AddCombination(ACTION_FIRE, GamepadInput.B)
            .AddCombination(ACTION_SPECIAL, GamepadInput.A, GamepadInput.B);

        _compositor = AddComponent(new UICompositor(cam) { DesignSize = DesignSize });
    }

    public override void PostInit()
    {
        base.PostInit();

        _keyboardPad = _gamepads.AddVirtualGamepad("Keyboard");

        var live = _compositor.CreateModule();
        live.Position = new Vector2(-400, 0);

        var panel = live.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(20),
            Spacing = 10
        });
        panel.Add(new Label("Gamepads") { TextScale = 0.3f });
        _live = panel.Add(new Label { Size = new Vector2(520, 420), Align = Origin.TopLeft, TextScale = 0.2f });

        TestChecks checks = RunChecks();

        var results = _compositor.CreateModule();
        results.Position = new Vector2(400, 0);

        var list = results.AddComponent(new StackPanel
        {
            Color = new Vector4(0.1f, 0.12f, 0.17f, 0.92f),
            Padding = new UIEdges(20),
            Spacing = 10
        });
        list.Add(new Label("Checked as the scene started") { TextScale = 0.3f });
        list.Add(new Label(checks.Describe())
        {
            Align = Origin.TopLeft,
            TextScale = 0.19f,
            Color = checks.Passed == checks.Count ? new Vector4(0.6f, 0.95f, 0.65f, 1.0f) : new Vector4(1.0f, 0.5f, 0.45f, 1.0f)
        });

        Engine.Graphics.ClearColor = new Vector4(0.22f, 0.27f, 0.36f, 1.0f);
    }

    public override void UpdateState(float dt)
    {
        // The keyboard's gamepad reads as whatever is held on the keyboard, before anybody asks it anything
        var keyboard = Engine.Input.Keyboard;

        _held.Clear();
        foreach (var (key, input) in Keys)
        {
            if (keyboard.IsDown(key))
                _held.Add(input);
        }
        _keyboardPad.Update(GamepadSnapshot.Holding(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_held)));

        base.UpdateState(dt);

        var text = new StringBuilder();
        foreach (Gamepad gamepad in _gamepads.Gamepads)
        {
            if (!gamepad.IsConnected)
                continue;

            text.Append(gamepad.Slot).Append("  ").Append(gamepad.Name).Append(gamepad.IsVirtual ? "  (not a device)" : string.Empty).Append('\n');

            text.Append("    held:    ");
            foreach (GamepadInput input in Enum.GetValues<GamepadInput>())
            {
                if (gamepad.IsDown(input))
                    text.Append(input).Append(' ');
            }

            text.Append("\n    actions: ");
            foreach (string action in gamepad.Bindings.Actions)
            {
                if (gamepad.IsDown(action))
                    text.Append(action).Append(' ');
            }

            text.Append($"\n    sticks:  {gamepad.LeftStick.X:0.00} {gamepad.LeftStick.Y:0.00}   {gamepad.RightStick.X:0.00} {gamepad.RightStick.Y:0.00}\n\n");
        }

        text.Append("last touched: ").Append(_gamepads.LastUsed?.Name ?? "none");
        _live.Text = text.ToString();
    }

    /// <summary>
    /// Everything the gamepads promise that doesn't take a gamepad to find out, on managers nobody else uses.
    /// </summary>
    private static TestChecks RunChecks()
    {
        var checks = new TestChecks("Gamepad test");
        uint Bits(params GamepadInput[] inputs) => inputs.Aggregate(0u, (mask, input) => mask | GamepadInputs.Bit(input));

        checks.Check("an action is on when one of its inputs is held", () =>
        {
            var bindings = new GamepadBindings().Bind(ACTION_JUMP, GamepadInput.A).Add(ACTION_JUMP, GamepadInput.DPadUp);

            return bindings.IsActive(ACTION_JUMP, Bits(GamepadInput.A)) && bindings.IsActive(ACTION_JUMP, Bits(GamepadInput.DPadUp))
                && !bindings.IsActive(ACTION_JUMP, Bits(GamepadInput.B)) && !bindings.IsActive(ACTION_JUMP, 0)
                && bindings.IsBound(ACTION_JUMP, GamepadInput.A) && bindings.CombinationsOf(ACTION_JUMP).Count == 2;
        });

        checks.Check("a combination needs all of its inputs, and takes over from what it is made of", () =>
        {
            var bindings = new GamepadBindings()
                .Bind(ACTION_JUMP, GamepadInput.A)
                .Bind(ACTION_FIRE, GamepadInput.B)
                .AddCombination(ACTION_SPECIAL, GamepadInput.A, GamepadInput.B);

            uint both = Bits(GamepadInput.A, GamepadInput.B);

            return bindings.IsActive(ACTION_SPECIAL, both) && !bindings.IsActive(ACTION_JUMP, both) && !bindings.IsActive(ACTION_FIRE, both)
                && bindings.IsActive(ACTION_JUMP, Bits(GamepadInput.A)) && !bindings.IsActive(ACTION_SPECIAL, Bits(GamepadInput.A));
        });

        checks.Check("rebinding takes the inputs away from whatever had them", () =>
        {
            var bindings = new GamepadBindings().Bind(ACTION_JUMP, GamepadInput.A).Bind(ACTION_FIRE, GamepadInput.B);
            bindings.Rebind(ACTION_FIRE, GamepadInput.A);

            return bindings.IsActive(ACTION_FIRE, Bits(GamepadInput.A)) && !bindings.IsActive(ACTION_JUMP, Bits(GamepadInput.A))
                && bindings.CombinationsOf(ACTION_JUMP).Count == 0 && bindings.Actions.Contains(ACTION_JUMP);
        });

        checks.Check("bindings can be taken apart again and described", () =>
        {
            var bindings = new GamepadBindings().Bind(ACTION_JUMP, GamepadInput.A).Add(ACTION_JUMP, GamepadInput.B);

            bool removed = bindings.Remove(ACTION_JUMP, GamepadInput.B) && !bindings.IsBound(ACTION_JUMP, GamepadInput.B);
            bool described = bindings.Describe(ACTION_JUMP).Contains('A');
            bool unbound = bindings.Unbind(ACTION_JUMP) && !bindings.Actions.Contains(ACTION_JUMP);

            return removed && described && unbound;
        });

        checks.Check("a copy of some bindings is its own", () =>
        {
            var original = new GamepadBindings().Bind(ACTION_JUMP, GamepadInput.A);
            GamepadBindings copy = original.Clone();
            copy.Rebind(ACTION_JUMP, GamepadInput.X);

            return original.IsBound(ACTION_JUMP, GamepadInput.A) && !copy.IsBound(ACTION_JUMP, GamepadInput.A) && copy.IsBound(ACTION_JUMP, GamepadInput.X);
        });

        checks.Check("a gamepad that isn't a device reads as what it is handed", () =>
        {
            var manager = new GamepadInputManager();
            Gamepad pad = manager.AddVirtualGamepad("Test");

            pad.Update(GamepadSnapshot.Holding(GamepadInput.A));
            bool pressed = pad.IsConnected && pad.IsVirtual && pad.IsDown(GamepadInput.A) && pad.WasPressed(GamepadInput.A) && pad.Pressed == GamepadInput.A && pad.AnyDown;

            pad.Update(GamepadSnapshot.Holding(GamepadInput.A));
            bool held = pad.IsDown(GamepadInput.A) && !pad.WasPressed(GamepadInput.A);

            pad.Update(default);
            return pressed && held && pad.WasReleased(GamepadInput.A) && !pad.AnyDown && pad.HeldMask == 0;
        });

        checks.Check("sticks and triggers are held once they are pushed far enough", () =>
        {
            var manager = new GamepadInputManager();
            Gamepad pad = manager.AddVirtualGamepad("Test");

            pad.Update(GamepadSnapshot.Holding(GamepadInput.LeftStickLeft, GamepadInput.RightTrigger));
            bool pushed = pad.IsDown(GamepadInput.LeftStickLeft) && pad.IsDown(GamepadInput.RightTrigger) && pad.LeftStick.X < -0.9f && pad.RightTrigger > 0.9f;

            // Barely off the middle is a stick that doesn't sit still, not somebody steering
            pad.Update(new GamepadSnapshot { LeftStick = new Vector2(-0.05f, 0.0f), RightTrigger = 0.05f });
            return pushed && !pad.IsDown(GamepadInput.LeftStickLeft) && !pad.IsDown(GamepadInput.RightTrigger) && pad.LeftStick == Vector2.Zero;
        });

        checks.Check("a gamepad starts out with the bindings everybody gets, and its own from then on", () =>
        {
            var manager = new GamepadInputManager();
            manager.DefaultBindings.Bind(ACTION_JUMP, GamepadInput.A);

            Gamepad first = manager.AddVirtualGamepad("First");
            Gamepad second = manager.AddVirtualGamepad("Second");
            second.Bindings.Rebind(ACTION_JUMP, GamepadInput.Y);

            first.Update(GamepadSnapshot.Holding(GamepadInput.A));
            second.Update(GamepadSnapshot.Holding(GamepadInput.A));
            bool own = first.IsDown(ACTION_JUMP) && first.WasPressed(ACTION_JUMP) && !second.IsDown(ACTION_JUMP);

            manager.ResetBindings();
            second.Update(default);
            second.Update(GamepadSnapshot.Holding(GamepadInput.A));
            return own && second.IsDown(ACTION_JUMP) && first.Slot == 0 && second.Slot == 1 && manager.TryGet(1, out Gamepad found) && found == second;
        });

        checks.Check("directions come out of four actions", () =>
        {
            var manager = new GamepadInputManager();
            manager.DefaultBindings
                .Bind("left", GamepadInput.DPadLeft).Bind("right", GamepadInput.DPadRight)
                .Bind("down", GamepadInput.DPadDown).Bind("up", GamepadInput.DPadUp);

            Gamepad pad = manager.AddVirtualGamepad("Test");
            pad.Update(GamepadSnapshot.Holding(GamepadInput.DPadRight, GamepadInput.DPadUp));

            Vector2 direction = pad.Direction("left", "right", "down", "up");
            return direction.X > 0 && direction.Y > 0;
        });

        checks.Check("everything is written as HIDL and read back the same", () =>
        {
            var manager = new GamepadInputManager();
            manager.DefaultBindings.Bind(ACTION_JUMP, GamepadInput.A).AddCombination(ACTION_SPECIAL, GamepadInput.A, GamepadInput.B);
            manager.DefaultBindings.Deadzone = 0.3f;

            Gamepad pad = manager.AddVirtualGamepad("Test");
            pad.Bindings.Rebind(ACTION_JUMP, GamepadInput.LeftTrigger);
            string written = manager.ToText();

            var other = new GamepadInputManager();
            Gamepad restored = other.AddVirtualGamepad("Test");
            bool loaded = other.LoadText(written, out List<string> problems);

            return loaded && problems.Count == 0 && other.ToText() == written
                && restored.Bindings.IsBound(ACTION_JUMP, GamepadInput.LeftTrigger) && !restored.Bindings.IsBound(ACTION_JUMP, GamepadInput.A)
                && other.DefaultBindings.IsActive(ACTION_SPECIAL, Bits(GamepadInput.A, GamepadInput.B)) && MathF.Abs(other.DefaultBindings.Deadzone - 0.3f) < 0.001f;
        });

        checks.Check("a file that makes no sense changes nothing and says so", () =>
        {
            var manager = new GamepadInputManager();
            manager.DefaultBindings.Bind(ACTION_JUMP, GamepadInput.A);
            string before = manager.ToText();

            bool garbage = manager.LoadText("let gamepads = {{{ not a file", out List<string> first);
            bool missing = manager.LoadText("let something_else = 1;", out List<string> second);

            return !garbage && first.Count > 0 && !missing && second.Count > 0 && manager.ToText() == before;
        });

        checks.Report();
        return checks;
    }
}
