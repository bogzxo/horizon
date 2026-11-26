using System.Diagnostics;
using System.Numerics;
using Horizon.Input;

using Silk.NET.Input;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Reads desktop controllers on the window thread and publishes platform-neutral snapshots. Sampled by the host every
/// time the window has heard the system, from <see cref="Start"/> until it is disposed.
/// </summary>
internal sealed class SilkGamepadSource : IPlatformGamepads
{
    private readonly IInputContext? context;
    private readonly Silk.NET.GLFW.Glfw? glfw;
    private readonly Action? customProbe;
    private readonly Func<bool>? isProbeReady;
    private readonly Action<Action> postToWindow;
    private readonly Action<SilkGamepadSource, bool>? sampled;
    private readonly List<DeviceEntry> devices = [];
    private readonly GlfwEntry?[] glfwDevices = new GlfwEntry?[16];
    private Silk.NET.GLFW.GlfwCallbacks.JoystickCallback? onJoystick;
    private int connectedGlfwCount;
    private bool started;
    private bool probeActivated;
    private volatile bool disposed;

    public event Action<InputEvent>? EventReceived;

    internal bool IsProbeActivated => probeActivated;
    internal int ConnectedCount => context is not null ? devices.Count : connectedGlfwCount;

    /// <param name="sampled">Told on the window thread when the host is to start sampling this and when to stop.</param>
    public SilkGamepadSource(IInputContext context, Action<Action> postToWindow, Action<SilkGamepadSource, bool>? sampled = null)
    {
        this.context = context;
        this.postToWindow = postToWindow;
        this.sampled = sampled;
        probeActivated = true;
    }

    /// <summary>Creates a GLFW gamepad source whose 16-slot probe is deferred until <paramref name="isProbeReady"/> is true or <see cref="ActivateProbe"/> is called.</summary>
    internal SilkGamepadSource(Silk.NET.GLFW.Glfw glfw, Action<Action> postToWindow, Action<SilkGamepadSource, bool>? sampled = null, Func<bool>? isProbeReady = null)
    {
        this.glfw = glfw;
        this.postToWindow = postToWindow;
        this.sampled = sampled;
        this.isProbeReady = isProbeReady;
    }

    /// <summary>Test seam for deferred probe activation without a native GLFW window.</summary>
    internal SilkGamepadSource(Action onProbe, Action<Action> postToWindow, Action<SilkGamepadSource, bool>? sampled = null, Func<bool>? isProbeReady = null)
    {
        customProbe = onProbe;
        this.postToWindow = postToWindow;
        this.sampled = sampled;
        this.isProbeReady = isProbeReady;
    }

    /// <summary>Starts on the window thread, after whoever listens has had a say (a script's pad first). From any thread.</summary>
    public void Start() => postToWindow(StartHere);

    /// <summary>Starts on the window thread after the event sink is attached.</summary>
    internal void StartHere()
    {
        if (started || disposed) return;
        started = true;
        sampled?.Invoke(this, true);
        if (context is not null)
        {
            context.ConnectionChanged += ConnectionChanged;
            foreach (var device in context.Gamepads)
                if (device.IsConnected) Connect(device);
            return;
        }

        if (isProbeReady is null || isProbeReady())
            ActivateProbe();
    }

    /// <summary>Runs the deferred GLFW 16-slot gamepad probe on the window thread after the first frame presents.</summary>
    internal void ActivateProbe()
    {
        if (!started || disposed || probeActivated || context is not null) return;
        probeActivated = true;

        if (customProbe is not null)
        {
            customProbe();
            return;
        }

        if (glfw is null) return;

        onJoystick = OnGlfwJoystick;
        glfw.SetJoystickCallback(onJoystick);
        for (int jid = 0; jid < 16; jid++)
        {
            if (glfw.JoystickPresent(jid) && glfw.JoystickIsGamepad(jid))
                ConnectGlfw(jid);
        }
    }

    /// <summary>Called after the window backend has refreshed its device state.</summary>
    public void Sample()
    {
        if (!started || disposed) return;
        if (context is not null)
        {
            for (int index = devices.Count - 1; index >= 0; index--)
            {
                var entry = devices[index];
                if (!entry.Native.IsConnected)
                {
                    Disconnect(entry.Native);
                    continue;
                }

                var snapshot = Read(entry);
                if (entry.HasSample && Same(snapshot, entry.LastSample)) continue;
                entry.LastSample = snapshot;
                entry.HasSample = true;
                Publish(InputEventKind.GamepadState, entry.Device, snapshot);
            }
            return;
        }

        if (!probeActivated || glfw is null || connectedGlfwCount == 0) return;
        SampleGlfw();
    }

    private void OnGlfwJoystick(int jid, Silk.NET.GLFW.ConnectedState state)
    {
        if (disposed || !probeActivated || (uint)jid >= 16) return;
        if (state == Silk.NET.GLFW.ConnectedState.Connected)
        {
            if (glfw!.JoystickIsGamepad(jid))
                ConnectGlfw(jid);
        }
        else
        {
            DisconnectGlfw(jid);
        }
    }

    private void ConnectGlfw(int jid)
    {
        if (glfwDevices[jid] is not null) return;
        string name = glfw!.GetGamepadName(jid) ?? $"Gamepad {jid}";
        var device = new GamepadDevice(name, static (_, _) => { });
        glfwDevices[jid] = new GlfwEntry(device);
        connectedGlfwCount++;
        Publish(InputEventKind.GamepadConnected, device);
    }

    private void DisconnectGlfw(int jid)
    {
        var entry = glfwDevices[jid];
        if (entry is null) return;
        glfwDevices[jid] = null;
        connectedGlfwCount--;
        Publish(InputEventKind.GamepadDisconnected, entry.Device);
    }

    private void SampleGlfw()
    {
        for (int jid = 0; jid < 16; jid++)
        {
            var entry = glfwDevices[jid];
            if (entry is null) continue;

            if (!glfw!.GetGamepadState(jid, out Silk.NET.GLFW.GamepadState state))
            {
                DisconnectGlfw(jid);
                continue;
            }

            GamepadSnapshot snapshot = ReadGlfw(state);
            if (entry.HasSample && Same(snapshot, entry.LastSample)) continue;
            entry.LastSample = snapshot;
            entry.HasSample = true;
            Publish(InputEventKind.GamepadState, entry.Device, snapshot);
        }
    }

    private static unsafe GamepadSnapshot ReadGlfw(Silk.NET.GLFW.GamepadState state)
    {
        GamepadSnapshot raw = default;
        if (state.Buttons[0] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.A);
        if (state.Buttons[1] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.B);
        if (state.Buttons[2] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.X);
        if (state.Buttons[3] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.Y);
        if (state.Buttons[4] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.LeftBumper);
        if (state.Buttons[5] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.RightBumper);
        if (state.Buttons[6] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.Back);
        if (state.Buttons[7] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.Start);
        if (state.Buttons[8] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.Home);
        if (state.Buttons[9] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.LeftStick);
        if (state.Buttons[10] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.RightStick);
        if (state.Buttons[11] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.DPadUp);
        if (state.Buttons[12] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.DPadRight);
        if (state.Buttons[13] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.DPadDown);
        if (state.Buttons[14] != 0) raw.Buttons |= GamepadInputs.Bit(GamepadInput.DPadLeft);

        raw.LeftStick = new Vector2(state.Axes[0], -state.Axes[1]);
        raw.RightStick = new Vector2(state.Axes[2], -state.Axes[3]);
        raw.LeftTrigger = (state.Axes[4] + 1.0f) * 0.5f;
        raw.RightTrigger = (state.Axes[5] + 1.0f) * 0.5f;
        return raw;
    }

    private void ConnectionChanged(IInputDevice device, bool connected)
    {
        if (disposed || device is not IGamepad gamepad) return;
        if (connected) Connect(gamepad);
        else Disconnect(gamepad);
    }

    private void Connect(IGamepad native)
    {
        if (devices.Exists(entry => ReferenceEquals(entry.Native, native))) return;
        var entry = new DeviceEntry(native);
        entry.Device = new GamepadDevice(native.Name, (left, right) => postToWindow(() =>
        {
            if (disposed || !entry.Active || !native.IsConnected) return;
            var motors = native.VibrationMotors;
            if (motors.Count > 0) motors[0].Speed = left;
            if (motors.Count > 1) motors[1].Speed = right;
        }));
        devices.Add(entry);
        Publish(InputEventKind.GamepadConnected, entry.Device);
    }

    private void Disconnect(IGamepad native)
    {
        int index = devices.FindIndex(entry => ReferenceEquals(entry.Native, native));
        if (index < 0) return;
        var entry = devices[index];
        entry.Active = false;
        devices.RemoveAt(index);
        Publish(InputEventKind.GamepadDisconnected, entry.Device);
    }

    private void Publish(InputEventKind kind, GamepadDevice device, GamepadSnapshot state = default) =>
        EventReceived?.Invoke(new InputEvent
        {
            Kind = kind, Gamepad = device, GamepadState = state, Timestamp = Stopwatch.GetTimestamp()
        });

    private static GamepadSnapshot Read(DeviceEntry entry)
    {
        var device = entry.Native;
        GamepadSnapshot raw = default;
        var buttons = device.Buttons;
        for (int index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            if (button.Pressed && TryFromButton(button.Name, out var input))
                raw.Buttons |= GamepadInputs.Bit(input);
        }

        // Devices say down is positive, the game says up is.
        var sticks = device.Thumbsticks;
        if (sticks.Count > 0) raw.LeftStick = new Vector2(sticks[0].X, -sticks[0].Y);
        if (sticks.Count > 1) raw.RightStick = new Vector2(sticks[1].X, -sticks[1].Y);
        var triggers = device.Triggers;
        if (triggers.Count > 0) raw.LeftTrigger = ReadTrigger(triggers[0].Position, ref entry.LeftFromMinusOne);
        if (triggers.Count > 1) raw.RightTrigger = ReadTrigger(triggers[1].Position, ref entry.RightFromMinusOne);
        return raw;
    }

    /// <summary>
    /// The input a button of the device is, if it is one this system knows.
    /// </summary>
    private static bool TryFromButton(ButtonName button, out GamepadInput input)
    {
        GamepadInput? found = button switch
        {
            ButtonName.A => GamepadInput.A,
            ButtonName.B => GamepadInput.B,
            ButtonName.X => GamepadInput.X,
            ButtonName.Y => GamepadInput.Y,
            ButtonName.LeftBumper => GamepadInput.LeftBumper,
            ButtonName.RightBumper => GamepadInput.RightBumper,
            ButtonName.Back => GamepadInput.Back,
            ButtonName.Start => GamepadInput.Start,
            ButtonName.Home => GamepadInput.Home,
            ButtonName.LeftStick => GamepadInput.LeftStick,
            ButtonName.RightStick => GamepadInput.RightStick,
            ButtonName.DPadUp => GamepadInput.DPadUp,
            ButtonName.DPadRight => GamepadInput.DPadRight,
            ButtonName.DPadDown => GamepadInput.DPadDown,
            ButtonName.DPadLeft => GamepadInput.DPadLeft,
            _ => null
        };

        input = found ?? default;
        return found is not null;
    }

    private static float ReadTrigger(float position, ref bool fromMinusOne)
    {
        if (position < 0.0f) fromMinusOne = true;
        return fromMinusOne ? (position + 1.0f) * 0.5f : position;
    }

    private static bool Same(in GamepadSnapshot left, in GamepadSnapshot right) =>
        left.Buttons == right.Buttons && left.LeftStick == right.LeftStick && left.RightStick == right.RightStick &&
        left.LeftTrigger == right.LeftTrigger && left.RightTrigger == right.RightTrigger;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        postToWindow(StopHere);
    }

    /// <summary>Let go of right here, by the host as it goes. Thread of the window.</summary>
    internal void DisposeHere()
    {
        disposed = true;
        StopHere();
    }

    // Let go of on the window thread, the only one that touches the context and the host's list
    private void StopHere()
    {
        if (!started) return;
        started = false;
        if (context is not null)
            context.ConnectionChanged -= ConnectionChanged;
        if (glfw is not null && onJoystick is not null)
        {
            glfw.SetJoystickCallback(null);
            onJoystick = null;
        }
        sampled?.Invoke(this, false);
    }

    private sealed class DeviceEntry(IGamepad native)
    {
        public IGamepad Native { get; } = native;
        public GamepadDevice Device { get; set; } = null!;
        public bool Active = true;
        public bool LeftFromMinusOne, RightFromMinusOne, HasSample;
        public GamepadSnapshot LastSample;
    }

    private sealed class GlfwEntry(GamepadDevice device)
    {
        public GamepadDevice Device { get; } = device;
        public bool HasSample;
        public GamepadSnapshot LastSample;
    }
}
