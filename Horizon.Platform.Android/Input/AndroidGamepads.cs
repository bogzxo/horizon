using System.Diagnostics;
using System.Numerics;

using Android.Content;
using Android.Hardware.Input;
using InputManager = global::Android.Hardware.Input.InputManager;
using Android.Views;

using Horizon.Input;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.Android;

/// <summary>
/// Controllers on a phone, Bluetooth or USB pads, read from the key and motion events Android hands the activity and
/// told apart from the keyboard by their InputDevice. Android's standard mapping for gamepads is what's read (the
/// buttons as KEYCODE_BUTTON_*, the left stick as AXIS_X and AXIS_Y, the right as AXIS_Z and AXIS_RZ, the triggers
/// as AXIS_LTRIGGER and AXIS_RTRIGGER or BRAKE and GAS, the d-pad as keys or as AXIS_HAT_X and HAT_Y), which is what
/// the system maps a known pad to through its key layout files.
/// <para>
/// One GamepadDevice a connection. Android's device id is the connection's, a pad that comes back gets a new one and
/// so a new device, and one that goes is published as gone with everything it held let go of. Plugging in and out is
/// heard through InputManager's device listener once <see cref="Start"/> got to the main thread. Everything here is
/// the main thread's. Rumble isn't done, a pad's vibrator is a later job, so devices are made without one.
/// </para>
/// </summary>
internal sealed class AndroidGamepads : IPlatformGamepads
{
    private readonly AndroidHost host;
    private readonly Dictionary<int, Pad> pads = [];
    private InputManager? manager;
    private Listener? listener;
    private bool started;
    private volatile bool disposed;

    public event Action<InputEvent>? EventReceived;

    public AndroidGamepads(AndroidHost host) => this.host = host;

    /// <summary>How many pads are connected, for a check.</summary>
    public int Count => pads.Count;

    public void Start() => host.Post(StartHere);

    private void StartHere()
    {
        if (started || disposed) return;
        started = true;

        manager = host.Context.GetSystemService(Context.InputService) as InputManager;
        listener = new Listener(this);
        manager?.RegisterInputDeviceListener(listener, null);
        foreach (int id in InputDevice.GetDeviceIds() ?? []) OnInputDeviceAdded(id);
    }

    /// <summary>A key event from a pad, true when it was one of its buttons and so ours. Main thread.</summary>
    public bool OnKey(KeyEvent key)
    {
        if (!started || disposed || Find(key.DeviceId, key.Device) is not { } pad) return false;

        // A trigger's click, which most pads (a DualSense among them) send next to the axis. The axis is what's read
        // where there is one and the key is only taken so it goes nowhere else, and it stands in for the axis on a pad
        // that hasn't got one. Taken twice over (key and axis) a pull would read as two presses
        if (key.KeyCode is Keycode.ButtonL2 or Keycode.ButtonR2) return OnTriggerKey(key, pad);

        // A touchpad's click and the numbered buttons are nobody's, taken so the system doesn't make something else of them
        if (key.KeyCode is >= Keycode.Button1 and <= Keycode.Button16) return true;

        if (ButtonOf(key.KeyCode) is not { } button) return false;

        // A repeat of a held button changes nothing
        if (key.RepeatCount > 0) return true;

        uint bit = GamepadInputs.Bit(button);
        bool down = key.Action == KeyEventActions.Down;
        uint before = pad.Keys;
        pad.Keys = down ? pad.Keys | bit : pad.Keys & ~bit;
        if (pad.Keys != before) Publish(InputEventKind.GamepadState, pad, Stamp(key.EventTime));
        return true;
    }

    private bool OnTriggerKey(KeyEvent key, Pad pad)
    {
        if (key.RepeatCount > 0) return true;
        bool left = key.KeyCode == Keycode.ButtonL2;
        if (pad.Native.GetMotionRange(left ? Axis.Ltrigger : Axis.Rtrigger) is not null || pad.Native.GetMotionRange(left ? Axis.Brake : Axis.Gas) is not null) return true;

        float pulled = key.Action == KeyEventActions.Down ? 1.0f : 0.0f;
        if (left) pad.LeftTrigger = pulled;
        else pad.RightTrigger = pulled;
        Publish(InputEventKind.GamepadState, pad, Stamp(key.EventTime));
        return true;
    }

    /// <summary>A motion event from a pad's sticks, triggers and hat, true when it was ours. Main thread.</summary>
    public bool OnMotion(MotionEvent motion)
    {
        if (!started || disposed || motion.ActionMasked != MotionEventActions.Move) return false;
        if (!motion.IsFromSource(InputSourceType.Joystick) && !motion.IsFromSource(InputSourceType.Gamepad)) return false;
        if (Find(motion.DeviceId, motion.Device) is not { } pad) return false;

        // Up is positive in the engine, down is on Android
        pad.LeftStick = new Vector2(Read(motion, pad, Axis.X), -Read(motion, pad, Axis.Y));
        pad.RightStick = new Vector2(Read(motion, pad, Axis.Z), -Read(motion, pad, Axis.Rz));
        pad.LeftTrigger = Math.Clamp(MathF.Max(Read(motion, pad, Axis.Ltrigger), Read(motion, pad, Axis.Brake)), 0.0f, 1.0f);
        pad.RightTrigger = Math.Clamp(MathF.Max(Read(motion, pad, Axis.Rtrigger), Read(motion, pad, Axis.Gas)), 0.0f, 1.0f);

        float hatX = Read(motion, pad, Axis.HatX), hatY = Read(motion, pad, Axis.HatY);
        pad.Hat = (hatX < -0.5f ? GamepadInputs.Bit(GamepadInput.DPadLeft) : 0) | (hatX > 0.5f ? GamepadInputs.Bit(GamepadInput.DPadRight) : 0) |
            (hatY < -0.5f ? GamepadInputs.Bit(GamepadInput.DPadUp) : 0) | (hatY > 0.5f ? GamepadInputs.Bit(GamepadInput.DPadDown) : 0);

        Publish(InputEventKind.GamepadState, pad, Stamp(motion.EventTime));
        return true;
    }

    /// <summary>Every pad let go of, for the app losing focus while buttons were held. Main thread.</summary>
    public void ReleaseAll()
    {
        foreach (Pad pad in pads.Values)
        {
            pad.Reset();
            Publish(InputEventKind.GamepadState, pad, Stopwatch.GetTimestamp());
        }
    }

    public void OnInputDeviceAdded(int deviceId)
    {
        if (disposed || pads.ContainsKey(deviceId)) return;
        if (InputDevice.GetDevice(deviceId) is not { } device || !IsGamepad(device)) return;

        var pad = new Pad(deviceId, new GamepadDevice(device.Name ?? $"Gamepad {deviceId}"), device);
        pads.Add(deviceId, pad);
        Horizon.Logging.Log.Info($"[Android] Gamepad '{pad.Device.Name}' connected (device {deviceId}, vendor {device.VendorId:x4}, product {device.ProductId:x4}).");
        Publish(InputEventKind.GamepadConnected, pad, Stopwatch.GetTimestamp());
    }

    public void OnInputDeviceRemoved(int deviceId)
    {
        if (!pads.Remove(deviceId, out Pad? pad)) return;
        Horizon.Logging.Log.Info($"[Android] Gamepad '{pad.Device.Name}' disconnected (device {deviceId}).");

        // Let go of what it held first, so nothing stays pressed on the slot it had
        pad.Reset();
        Publish(InputEventKind.GamepadState, pad, Stopwatch.GetTimestamp());
        Publish(InputEventKind.GamepadDisconnected, pad, Stopwatch.GetTimestamp());
    }

    public void OnInputDeviceChanged(int deviceId)
    {
        // A pad whose axes changed (some report theirs late) is looked at again for its ranges
        if (pads.TryGetValue(deviceId, out Pad? pad) && InputDevice.GetDevice(deviceId) is { } device) pad.Native = device;
        else OnInputDeviceAdded(deviceId);
    }

    // A pad already known, or one that wasn't heard connecting (it was there before the listener) found by its events
    private Pad? Find(int deviceId, InputDevice? device)
    {
        if (pads.TryGetValue(deviceId, out Pad? pad)) return pad;
        if (device is null || !IsGamepad(device)) return null;
        OnInputDeviceAdded(deviceId);
        return pads.GetValueOrDefault(deviceId);
    }

    private static bool IsGamepad(InputDevice device) =>
        !device.IsVirtual && (((int)device.Sources & (int)InputSourceType.Gamepad) == (int)InputSourceType.Gamepad ||
            ((int)device.Sources & (int)InputSourceType.Joystick) == (int)InputSourceType.Joystick);

    private static float Read(MotionEvent motion, Pad pad, Axis axis)
    {
        // An axis the device hasn't got reads 0 anyway, asking the range first keeps a hat-less pad's d-pad keys alone
        if (pad.Native.GetMotionRange(axis, motion.Source) is null) return 0.0f;
        return motion.GetAxisValue(axis);
    }

    private static GamepadInput? ButtonOf(Keycode code) => code switch
    {
        Keycode.ButtonA => GamepadInput.A,
        Keycode.ButtonB => GamepadInput.B,
        Keycode.ButtonX => GamepadInput.X,
        Keycode.ButtonY => GamepadInput.Y,
        Keycode.ButtonL1 => GamepadInput.LeftBumper,
        Keycode.ButtonR1 => GamepadInput.RightBumper,
        Keycode.ButtonSelect => GamepadInput.Back,
        Keycode.Back => GamepadInput.Back,
        Keycode.ButtonStart => GamepadInput.Start,
        Keycode.ButtonMode => GamepadInput.Home,
        Keycode.ButtonThumbl => GamepadInput.LeftStick,
        Keycode.ButtonThumbr => GamepadInput.RightStick,
        Keycode.DpadUp => GamepadInput.DPadUp,
        Keycode.DpadRight => GamepadInput.DPadRight,
        Keycode.DpadDown => GamepadInput.DPadDown,
        Keycode.DpadLeft => GamepadInput.DPadLeft,
        Keycode.DpadCenter => GamepadInput.A,
        _ => null
    };

    private void Publish(InputEventKind kind, Pad pad, long stamp)
    {
        GamepadSnapshot state = default;
        state.Buttons = pad.Keys | pad.Hat;
        state.LeftStick = pad.LeftStick;
        state.RightStick = pad.RightStick;
        state.LeftTrigger = pad.LeftTrigger;
        state.RightTrigger = pad.RightTrigger;
        EventReceived?.Invoke(new InputEvent { Kind = kind, Gamepad = pad.Device, GamepadState = state, Timestamp = stamp });
        host.HeardInput();
    }

    private static long Stamp(long uptime) => AndroidPointers.Stamp(uptime);

    /// <summary>Stops listening. From any thread, the listener is taken off on the main thread.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        host.Post(() =>
        {
            if (listener is not null) manager?.UnregisterInputDeviceListener(listener);
            listener?.Dispose();
            listener = null;
            manager = null;
            pads.Clear();
        });
    }

    // What InputManager calls on the main thread (the handler given is null, so the thread that registered), a Java
    // object of its own so the feed itself isn't one
    private sealed class Listener(AndroidGamepads gamepads) : Java.Lang.Object, InputManager.IInputDeviceListener
    {
        public void OnInputDeviceAdded(int deviceId) => gamepads.OnInputDeviceAdded(deviceId);
        public void OnInputDeviceRemoved(int deviceId) => gamepads.OnInputDeviceRemoved(deviceId);
        public void OnInputDeviceChanged(int deviceId) => gamepads.OnInputDeviceChanged(deviceId);
    }

    private sealed class Pad(int id, GamepadDevice device, InputDevice native)
    {
        public int Id { get; } = id;
        public GamepadDevice Device { get; } = device;
        public InputDevice Native { get; set; } = native;
        public uint Keys, Hat;
        public Vector2 LeftStick, RightStick;
        public float LeftTrigger, RightTrigger;

        public void Reset()
        {
            Keys = Hat = 0;
            LeftStick = RightStick = Vector2.Zero;
            LeftTrigger = RightTrigger = 0.0f;
        }
    }
}
